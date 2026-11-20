using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Downloads;

public sealed class DownloadOrchestratorTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly DefaultAppPaths _paths;
    private readonly string _outDir;

    public DownloadOrchestratorTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ytdl_orch_{Guid.NewGuid():N}");
        _paths = new DefaultAppPaths(
            Path.Combine(_sandboxRoot, "app"),
            Path.Combine(_sandboxRoot, "local"),
            Path.Combine(_sandboxRoot, "roaming"),
            Path.Combine(_sandboxRoot, "videos"));
        _paths.EnsureDirectoriesExist();
        _outDir = Path.Combine(_sandboxRoot, "out");
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task StandardVodDownload_UsesIsolatedTempDir_KeepsSaveFolderClean_AndPersistsHistory()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        string? capturedTempDir = null;

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            var argList = req.Arguments.ToList();
            var homeDir = ExtractPrefixedArg(argList, "home:");
            var tempDir = ExtractPrefixedArg(argList, "temp:");
            var outTemplate = argList[argList.IndexOf("-o") + 1];
            capturedTempDir = tempDir;

            Assert.Equal(Path.GetFullPath(_outDir), homeDir);
            Assert.StartsWith(_paths.TempDownloadsDirectory, tempDir, StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.IsPathRooted(outTemplate));

            // Simulate fragment files written strictly into job temp dir — Save folder stays empty!
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "frag1.part-Frag1"), new byte[256]);
            Assert.Empty(Directory.GetFiles(_outDir));

            var mp4Path = Path.Combine(homeDir, outTemplate.Replace(".%(ext)s", ".mp4", StringComparison.Ordinal));

            req.OnStandardOutput?.Invoke("YTDL_PROG|downloading|5000000|10000000|NA|2000000|2|NA|NA|avc1|none|137");
            req.OnStandardOutput?.Invoke("YTDL_PROG|finished|10000000|10000000|NA|2000000|0|NA|NA|avc1|none|137");
            req.OnStandardOutput?.Invoke("YTDL_PROG|downloading|1000000|2000000|NA|1000000|1|NA|NA|none|mp4a|140");
            req.OnStandardOutput?.Invoke("YTDL_PP|started|Merger");
            await File.WriteAllBytesAsync(mp4Path, new byte[8192]);
            req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");

            return new ProcessRunResult(0, false, false, false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

        var snapshots = new List<DownloadProgressSnapshot>();
        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            snapshots.Add(s);
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
                doneTcs.TrySetResult(s);
        });

        var q = new QualityOption("video-1080-std", "1080p", false, 1080, 30, "H.264", 12_000_000, "bv*[height<=1080]+ba/b", null, true, ["mp4", "mkv"]);
        var req = new DownloadJobRequest("vod-1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Test VOD", "Channel", null, _outDir, q, "mp4", MediaLiveStatus.NotLive, KnownDurationSeconds: 212);

        await orch.EnqueueAsync(req, progress);
        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(DownloadJobState.Completed, final.State);
        Assert.Equal(100, final.Percentage);
        Assert.Contains(snapshots, s => s.PhaseLabel.Contains("video", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshots, s => s.PhaseLabel.Contains("audio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshots, s => s.State == DownloadJobState.Finalizing);
        Assert.Single(Directory.GetFiles(_outDir));
        Assert.NotNull(capturedTempDir);
        Assert.False(Directory.Exists(capturedTempDir), "Per-job temp folder must be deleted after completion.");
        Assert.Single(await history.LoadAsync());
    }

    [Fact]
    public async Task StandardVodCancel_DeletesJobTempFolderAndLeavesSaveFolderEmpty()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        string? jobTempDir = null;
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, ct) =>
        {
            var argList = req.Arguments.ToList();
            jobTempDir = ExtractPrefixedArg(argList, "temp:");
            var outTemplate = argList[argList.IndexOf("-o") + 1];
            var relBase = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);

            await File.WriteAllBytesAsync(Path.Combine(jobTempDir, relBase + ".f137.mp4.part"), new byte[1024], CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(jobTempDir, relBase + ".f137.mp4.part-Frag4"), new byte[512], CancellationToken.None);
            startedTcs.TrySetResult();

            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
            return new ProcessRunResult(-1, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromMilliseconds(50), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("video-1080-std", "1080p", false, 1080, 30, "H.264", null, "bv*+ba/b", null, true, ["mp4"]);
        var req = new DownloadJobRequest("vod-cancel", "abc12345678", "https://www.youtube.com/watch?v=abc12345678", "Cancel Test", "Chan", null, _outDir, q, "mp4", MediaLiveStatus.NotLive);

        await orch.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Cancelled or DownloadJobState.Failed or DownloadJobState.Completed)
                doneTcs.TrySetResult(s);
        }));

        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(orch.RequestCancel("vod-cancel"));

        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Cancelled, final.State);
        Assert.Empty(Directory.GetFiles(_outDir));
        Assert.NotNull(jobTempDir);
        Assert.False(Directory.Exists(jobTempDir), "Job temp folder must be deleted when a VOD download is cancelled.");
    }

    [Fact]
    public async Task LivestreamModeA_StopAndSave_RemuxesInTempAndMovesFinalMp4ToSaveFolder()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? jobTempDir = null;

        var runner = new DelegateProcessRunner(async (req, ct) =>
        {
            if (req.ExecutablePath.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
            {
                var argList = req.Arguments.ToList();
                jobTempDir = ExtractPrefixedArg(argList, "temp:");
                var outTemplate = argList[argList.IndexOf("-o") + 1];
                var relBase = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);
                var tsPath = Path.Combine(jobTempDir, relBase + ".mkv.part");

                req.OnStandardError?.Invoke("frame=  120 fps=30 q=-1.0 size=   2048KiB time=00:00:04.00 bitrate= 4100kbits/s");
                await File.WriteAllBytesAsync(tsPath, new byte[4096], CancellationToken.None);
                startedTcs.TrySetResult();

                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
                return new ProcessRunResult(0, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
            }

            // FFmpeg remux to mp4 must happen inside jobTempDir, keeping Save folder clean!
            Assert.StartsWith(_paths.TempDownloadsDirectory, req.Arguments[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(_outDir));
            await File.WriteAllBytesAsync(req.Arguments[^1], new byte[4096], CancellationToken.None);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("video-720-std", "720p", false, 720, 60, "H.264", null, "b[height<=720]/b", null, false, ["mkv", "mp4"]);
        var req = new DownloadJobRequest(
            "live-a", "live1234567", "https://www.youtube.com/watch?v=live1234567", "Live Mode A", "Chan", null, _outDir,
            q, "mp4", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromNow);

        await orch.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
                doneTcs.TrySetResult(s);
        }));

        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        orch.RequestCancel("live-a");

        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Completed, final.State);
        Assert.Contains("stopped & saved", final.PhaseLabel, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(final.OutputFilePath);
        Assert.StartsWith(_outDir, final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".mp4", final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(final.OutputFilePath));
        Assert.Single(Directory.GetFiles(_outDir));
        Assert.NotNull(jobTempDir);
        Assert.False(Directory.Exists(jobTempDir));
    }

    [Fact]
    public async Task LivestreamModeB_WhenDvrDisabled_FailsWithClearActionableMessage()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var runner = new DelegateProcessRunner((_, _) => throw new InvalidOperationException("yt-dlp must not launch when DVR is disabled."));
        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("best", "Best", false, 1080, 60, "H.264", null, "bv*+ba/b", null, true, ["mkv"]);
        var req = new DownloadJobRequest(
            "live-nodvr", "live1234567", "https://www.youtube.com/watch?v=live1234567", "No DVR Stream", "Chan", null, _outDir,
            q, "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromStartUntilEnd, IsDvrAvailable: false);

        await orch.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Failed or DownloadJobState.Completed)
                doneTcs.TrySetResult(s);
        }));

        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Failed, final.State);
        Assert.Contains("Mode A", final.ErrorMessage);
    }

    [Fact]
    public async Task LivestreamModeC_WhenPartialTracksLeftInTemp_FallbackMuxesAndMovesToSaveFolder()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var manualMuxInvoked = false;
        string? jobTempDir = null;

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            if (req.ExecutablePath.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
            {
                var argList = req.Arguments.ToList();
                jobTempDir = ExtractPrefixedArg(argList, "temp:");
                var outTemplate = argList[argList.IndexOf("-o") + 1];
                var relBase = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);

                // Simulate interrupted download leaving separate .f134 + .f140 partial tracks strictly inside jobTempDir
                await File.WriteAllBytesAsync(Path.Combine(jobTempDir, relBase + ".f134.mp4.part"), new byte[8192]);
                await File.WriteAllBytesAsync(Path.Combine(jobTempDir, relBase + ".f140.m4a.part"), new byte[2048]);
                Assert.Empty(Directory.GetFiles(_outDir));

                return new ProcessRunResult(1, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
            }

            if (req.Arguments.Contains("-shortest"))
            {
                manualMuxInvoked = true;
                Assert.StartsWith(_paths.TempDownloadsDirectory, req.Arguments[^1], StringComparison.OrdinalIgnoreCase);
                await File.WriteAllBytesAsync(req.Arguments[^1], new byte[10240]);
                return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
            }

            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("video-360", "360p", false, 360, 30, "H.264", null, "134+140", null, true, ["mkv"]);
        var req = new DownloadJobRequest(
            "live-fallback", "live1234567", "https://www.youtube.com/watch?v=live1234567", "Fallback Stream", "Chan", null, _outDir,
            q, "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromStartToLiveEdge,
            LiveEdgeMarginSeconds: 30, LivestreamStartUtc: DateTimeOffset.UtcNow.AddMinutes(-10), IsDvrAvailable: true);

        await orch.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed)
                doneTcs.TrySetResult(s);
        }));

        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Completed, final.State);
        Assert.True(manualMuxInvoked);
        Assert.NotNull(final.OutputFilePath);
        Assert.StartsWith(_outDir, final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".mkv", final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(final.OutputFilePath));
        Assert.Single(Directory.GetFiles(_outDir));
        Assert.NotNull(jobTempDir);
        Assert.False(Directory.Exists(jobTempDir));
    }

    [Fact]
    public async Task PruneOrphanedTempDirectories_CleansUpStaleJobFolders()
    {
        var orphanDir = Path.Combine(_paths.TempDownloadsDirectory, "stale-crash-job-123");
        Directory.CreateDirectory(orphanDir);
        await File.WriteAllBytesAsync(Path.Combine(orphanDir, "leftover.part-Frag99"), new byte[128]);

        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var runner = new DelegateProcessRunner((_, _) => Task.FromResult(new ProcessRunResult(0, false, false, false, TimeSpan.Zero, "", "")));

        await using var orch = new DownloadOrchestrator(_paths, new StubToolManager(CreateFakeToolchain()), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);
        orch.PruneOrphanedTempDirectories();

        Assert.False(Directory.Exists(orphanDir));
    }

    [Fact]
    public void AudioOnlyArguments_UseFFmpegExtractAudioFlagsAndIsolatedTempPaths()
    {
        var tc = CreateFakeToolchain();
        var audioQ = new QualityOption("audio-mp3", "Audio Only — MP3", true, null, null, "MP3", null, "ba/b", "mp3", true, ["mp3"]);
        var req = new DownloadJobRequest("a1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Song", "Artist", null, _outDir, audioQ, "mp3", MediaLiveStatus.NotLive);
        var jobTemp = Path.Combine(_paths.TempDownloadsDirectory, "a1");

        var args = YtDlpArgumentBuilder.BuildStandardDownloadArguments(tc, req, _outDir, jobTemp, "Song [dQw4w9WgXcQ]");
        Assert.Contains("-x", args);
        Assert.Contains("--audio-format", args);
        Assert.Contains("mp3", args);
        Assert.Contains("--audio-quality", args);
        Assert.Contains($"home:{Path.GetFullPath(_outDir)}", args);
        Assert.Contains($"temp:{Path.GetFullPath(jobTemp)}", args);
        Assert.Equal("Song [dQw4w9WgXcQ].%(ext)s", args[args.ToList().IndexOf("-o") + 1]);
    }

    [Fact]
    public async Task ServiceProvider_WithDownloadOrchestrator_DisposesCleanlyBothAsyncAndSync()
    {
        ServiceProvider BuildRealProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAppPaths>(_paths);
            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddProvider(new RollingFileLoggerProvider(_paths.LogsDirectory, LogLevel.Information));
            });
            services.AddSingleton(_ => new HttpClient());
            services.AddSingleton<IProcessRunner, SafeProcessRunner>();
            services.AddSingleton<ISettingsStore, SettingsStore>();
            services.AddSingleton<IHistoryStore, HistoryStore>();
            services.AddSingleton<IToolManager, ToolManager>();
            services.AddSingleton<OutputPathGuard>();
            services.AddSingleton<IMediaProbeService, MediaProbeService>();
            services.AddSingleton<IDownloadOrchestrator, DownloadOrchestrator>();
            return services.BuildServiceProvider();
        }

        // 1. Async container disposal (App.OnExit primary path)
        await using (var asyncProvider = BuildRealProvider())
        {
            var orch1 = asyncProvider.GetRequiredService<IDownloadOrchestrator>();
            Assert.NotNull(orch1);
            await asyncProvider.DisposeAsync();
        }

        // 2. Synchronous container disposal (must NEVER throw InvalidOperationException for IAsyncDisposable-only)
        using (var syncProvider = BuildRealProvider())
        {
            var orch2 = syncProvider.GetRequiredService<IDownloadOrchestrator>();
            Assert.NotNull(orch2);
            var ex = Record.Exception(() => syncProvider.Dispose());
            Assert.Null(ex);
        }

        // 3. Idempotent double disposal on orchestrator directly (DisposeAsync + Dispose)
        using var provider3 = BuildRealProvider();
        var orch3 = provider3.GetRequiredService<IDownloadOrchestrator>();
        await orch3.DisposeAsync();
        var doubleDisposeEx = Record.Exception(() => orch3.Dispose());
        Assert.Null(doubleDisposeEx);
    }

    private static string ExtractPrefixedArg(List<string> args, string prefix)
    {
        var match = args.First(a => a.StartsWith(prefix, StringComparison.Ordinal));
        return match[prefix.Length..];
    }

    private static ResolvedToolchain CreateFakeToolchain() => new(
        @"C:\fake\yt-dlp.exe", @"C:\fake", @"C:\fake\ffmpeg.exe", @"C:\fake\ffprobe.exe", @"C:\fake\deno.exe",
        "2026.10.26", "8.1", "2.9.8");

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class DelegateProcessRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) => handler(request, ct);
        public void Dispose() { }
    }

    private sealed class StubToolManager(ResolvedToolchain tc) : IToolManager
    {
        public Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default) => Task.FromResult(tc);
        public Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolStatusInfo>>([]);
        public Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
