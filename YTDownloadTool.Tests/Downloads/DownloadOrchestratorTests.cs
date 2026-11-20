using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
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
    public async Task StandardVodDownload_TracksMultiStreamProgressAndPersistsHistory()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            var outTemplate = req.Arguments[req.Arguments.ToList().IndexOf("-o") + 1];
            var mp4Path = outTemplate.Replace(".%(ext)s", ".mp4", StringComparison.Ordinal);

            req.OnStandardOutput?.Invoke("YTDL_PROG|downloading|5000000|10000000|NA|2000000|2|NA|NA|avc1|none|137");
            req.OnStandardOutput?.Invoke("YTDL_PROG|finished|10000000|10000000|NA|2000000|0|NA|NA|avc1|none|137");
            req.OnStandardOutput?.Invoke("YTDL_PROG|downloading|1000000|2000000|NA|1000000|1|NA|NA|none|mp4a|140");
            req.OnStandardOutput?.Invoke("YTDL_PP|started|Merger");
            await File.WriteAllBytesAsync(mp4Path, new byte[8192]);
            req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");

            return new ProcessRunResult(0, false, false, false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

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
        Assert.Single(await history.LoadAsync());
    }

    [Fact]
    public async Task StandardVodCancel_CleansUpPartialTempFiles()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        string? createdPartPath = null;
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, ct) =>
        {
            var outTemplate = req.Arguments[req.Arguments.ToList().IndexOf("-o") + 1];
            var basePath = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);
            createdPartPath = basePath + ".f137.mp4.part";
            await File.WriteAllBytesAsync(createdPartPath, new byte[1024], CancellationToken.None);
            startedTcs.TrySetResult();

            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
            return new ProcessRunResult(-1, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromMilliseconds(50), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

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
        Assert.NotNull(createdPartPath);
        Assert.False(File.Exists(createdPartPath), "Partial .part file should be cleaned up when a VOD download is cancelled.");
    }

    [Fact]
    public async Task LivestreamModeA_StopAndSave_PreservesCapturedRecording()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, ct) =>
        {
            if (req.ExecutablePath.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
            {
                var outTemplate = req.Arguments[req.Arguments.ToList().IndexOf("-o") + 1];
                var basePath = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);
                var tsPath = basePath + ".mkv";

                req.OnStandardError?.Invoke("frame=  120 fps=30 q=-1.0 size=   2048KiB time=00:00:04.00 bitrate= 4100kbits/s");
                await File.WriteAllBytesAsync(tsPath, new byte[4096], CancellationToken.None);
                startedTcs.TrySetResult();

                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
                return new ProcessRunResult(0, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
            }

            // FFmpeg remux to mp4
            await File.WriteAllBytesAsync(req.Arguments[^1], new byte[4096], CancellationToken.None);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

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
        Assert.True(File.Exists(final.OutputFilePath));
    }

    [Fact]
    public async Task LivestreamModeB_WhenDvrDisabled_FailsWithClearActionableMessage()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var runner = new DelegateProcessRunner((_, _) => throw new InvalidOperationException("yt-dlp must not launch when DVR is disabled."));
        await using var orch = new DownloadOrchestrator(new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

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
    public async Task LivestreamModeC_WhenPartialTracksLeftUnmerged_FallbackMuxesWithFFmpeg()
    {
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var history = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var tc = CreateFakeToolchain();

        var manualMuxInvoked = false;
        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            if (req.ExecutablePath.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
            {
                var outTemplate = req.Arguments[req.Arguments.ToList().IndexOf("-o") + 1];
                var basePath = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);

                // Simulate interrupted download leaving separate .f134.mp4.part and .f140.m4a.part files
                await File.WriteAllBytesAsync(basePath + ".f134.mp4.part", new byte[8192]);
                await File.WriteAllBytesAsync(basePath + ".f140.m4a.part", new byte[2048]);
                return new ProcessRunResult(1, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
            }

            if (req.Arguments.Contains("-shortest"))
            {
                manualMuxInvoked = true;
                await File.WriteAllBytesAsync(req.Arguments[^1], new byte[10240]);
                return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
            }

            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        await using var orch = new DownloadOrchestrator(new StubToolManager(tc), runner, settings, history, new OutputPathGuard(), NullLogger<DownloadOrchestrator>.Instance);

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
        Assert.EndsWith(".mkv", final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(final.OutputFilePath));
    }

    [Fact]
    public void AudioOnlyArguments_UseFFmpegExtractAudioFlags()
    {
        var tc = CreateFakeToolchain();
        var audioQ = new QualityOption("audio-mp3", "Audio Only — MP3", true, null, null, "MP3", null, "ba/b", "mp3", true, ["mp3"]);
        var req = new DownloadJobRequest("a1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Song", "Artist", null, _outDir, audioQ, "mp3", MediaLiveStatus.NotLive);

        var args = YtDlpArgumentBuilder.BuildStandardDownloadArguments(tc, req, Path.Combine(_outDir, "Song [dQw4w9WgXcQ]"));
        Assert.Contains("-x", args);
        Assert.Contains("--audio-format", args);
        Assert.Contains("mp3", args);
        Assert.Contains("--audio-quality", args);
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
