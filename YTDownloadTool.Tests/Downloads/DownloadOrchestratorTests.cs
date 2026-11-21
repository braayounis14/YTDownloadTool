using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    private sealed class TestStack : IAsyncDisposable
    {
        public required SqliteDatabaseManager Db { get; init; }
        public required SettingsStore Settings { get; init; }
        public required SqliteHistoryStore History { get; init; }
        public required SqliteDownloadJobRepository Jobs { get; init; }
        public required SqliteDiagnosticsRepository Diagnostics { get; init; }
        public required DiskSpaceReservationLedger Ledger { get; init; }
        public required DownloadOrchestrator Orchestrator { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Orchestrator.DisposeAsync();
            Db.Dispose();
        }
    }

    private async Task<TestStack> CreateStackAsync(
        IProcessRunner runner,
        IMediaProbeService? probeService = null,
        IMediaIntegrityService? integrityService = null,
        DiskSpaceReservationLedger? ledger = null,
        int maxConcurrent = 2,
        Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null)
    {
        var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();

        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        var loadedSettings = await settings.LoadAsync();
        loadedSettings.MaxConcurrentDownloads = maxConcurrent;
        await settings.SaveAsync(loadedSettings);

        var history = new SqliteHistoryStore(db, NullLogger<SqliteHistoryStore>.Instance);
        var jobs = new SqliteDownloadJobRepository(db, NullLogger<SqliteDownloadJobRepository>.Instance);
        var diagnostics = new SqliteDiagnosticsRepository(db, NullLogger<SqliteDiagnosticsRepository>.Instance);
        var effectiveLedger = ledger ?? new DiskSpaceReservationLedger(_ => 500L * 1024 * 1024 * 1024);
        var effectiveProbe = probeService ?? new StubMediaProbeService();
        var effectiveIntegrity = integrityService ?? new StubMediaIntegrityService();

        var orch = new DownloadOrchestrator(
            _paths,
            new StubToolManager(CreateFakeToolchain()),
            runner,
            settings,
            history,
            new OutputPathGuard(),
            jobs,
            diagnostics,
            effectiveLedger,
            effectiveProbe,
            effectiveIntegrity,
            NullLogger<DownloadOrchestrator>.Instance,
            retryDelayAsync);

        return new TestStack
        {
            Db = db,
            Settings = settings,
            History = history,
            Jobs = jobs,
            Diagnostics = diagnostics,
            Ledger = effectiveLedger,
            Orchestrator = orch
        };
    }

    [Fact]
    public async Task StandardVodDownload_UsesIsolatedTempDir_KeepsSaveFolderClean_AndPersistsVerifiedHistory()
    {
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

        await using var stack = await CreateStackAsync(runner);

        var snapshots = new List<DownloadProgressSnapshot>();
        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            snapshots.Add(s);
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
                doneTcs.TrySetResult(s);
        });

        var q = MakeVodQuality();
        var req = new DownloadJobRequest("vod-1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Test VOD", "Channel", null, _outDir, q, "mp4", MediaLiveStatus.NotLive, KnownDurationSeconds: 212);

        await stack.Orchestrator.EnqueueAsync(req, progress);
        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(DownloadJobState.Completed, final.State);
        Assert.Equal(100, final.Percentage);
        Assert.Equal(MediaIntegrityStatus.Verified, final.IntegrityStatus);
        Assert.Contains(snapshots, s => s.PhaseLabel.Contains("video", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshots, s => s.PhaseLabel.Contains("audio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshots, s => s.State == DownloadJobState.Finalizing);
        Assert.Single(Directory.GetFiles(_outDir));
        Assert.NotNull(capturedTempDir);
        Assert.False(Directory.Exists(capturedTempDir), "Per-job temp folder must be deleted after completion.");

        var historyItems = await stack.History.LoadAsync();
        Assert.Single(historyItems);
        Assert.Equal(MediaIntegrityStatus.Verified, historyItems[0].IntegrityStatus);
        Assert.Equal("1920x1080", historyItems[0].Resolution);
    }

    [Fact]
    public async Task StandardVodCancel_DeletesJobTempFolderAndLeavesSaveFolderEmpty()
    {
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

        await using var stack = await CreateStackAsync(runner);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new DownloadJobRequest("vod-cancel", "abc12345678", "https://www.youtube.com/watch?v=abc12345678", "Cancel Test", "Chan", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive);

        await stack.Orchestrator.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Cancelled or DownloadJobState.Failed or DownloadJobState.Completed)
                doneTcs.TrySetResult(s);
        }));

        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stack.Orchestrator.RequestCancel("vod-cancel"));

        var final = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Cancelled, final.State);
        Assert.Empty(Directory.GetFiles(_outDir));
        Assert.NotNull(jobTempDir);
        Assert.False(Directory.Exists(jobTempDir), "Job temp folder must be deleted when a VOD download is cancelled.");
    }

    [Fact]
    public async Task VodPauseAndResume_PreservesTempPartFiles_AndResumesWithContinueAndSameTemplate()
    {
        var runCount = 0;
        string? firstTempDir = null;
        string? firstOutputTemplate = null;
        string? secondTempDir = null;
        string? secondOutputTemplate = null;
        bool secondHadContinue = false;
        var startedFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, ct) =>
        {
            runCount++;
            var argList = req.Arguments.ToList();
            var tempDir = ExtractPrefixedArg(argList, "temp:");
            var homeDir = ExtractPrefixedArg(argList, "home:");
            var outTemplate = argList[argList.IndexOf("-o") + 1];
            var relBase = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);

            if (runCount == 1)
            {
                firstTempDir = tempDir;
                firstOutputTemplate = outTemplate;
                await File.WriteAllBytesAsync(Path.Combine(tempDir, relBase + ".f137.mp4.part"), new byte[4096], CancellationToken.None);
                req.OnStandardOutput?.Invoke("YTDL_PROG|downloading|4000000|10000000|NA|1000000|6|NA|NA|avc1|none|137");
                startedFirstRun.TrySetResult();

                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
                return new ProcessRunResult(-1, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromMilliseconds(50), string.Empty, string.Empty);
            }

            secondTempDir = tempDir;
            secondOutputTemplate = outTemplate;
            secondHadContinue = argList.Contains("--continue");

            // Confirm partial file from run 1 is still right here in tempDir!
            Assert.True(File.Exists(Path.Combine(tempDir, relBase + ".f137.mp4.part")));

            var mp4Path = Path.Combine(homeDir, relBase + ".mp4");
            await File.WriteAllBytesAsync(mp4Path, new byte[8192], CancellationToken.None);
            req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(50), string.Empty, string.Empty);
        });

        await using var stack = await CreateStackAsync(runner);

        var pausedTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State == DownloadJobState.Paused) pausedTcs.TrySetResult(s);
            if (s.State == DownloadJobState.Completed) completedTcs.TrySetResult(s);
        });

        var req = new DownloadJobRequest("vod-pause", "pau12345678", "https://www.youtube.com/watch?v=pau12345678", "Pause Test", "Chan", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive);
        await stack.Orchestrator.EnqueueAsync(req, progress);

        await startedFirstRun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await stack.Orchestrator.PauseJobAsync("vod-pause"));

        var pausedSnap = await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadJobState.Paused, pausedSnap.State);
        Assert.NotNull(firstTempDir);
        Assert.True(Directory.Exists(firstTempDir), "Temp folder MUST be preserved when VOD is paused.");
        Assert.Equal(0, stack.Ledger.GetReservedBytesForDrive(_outDir));

        // Resume!
        Assert.True(await stack.Orchestrator.ResumeJobAsync("vod-pause", progress));
        var doneSnap = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(DownloadJobState.Completed, doneSnap.State);
        Assert.True(secondHadContinue, "Resumed VOD must pass --continue to yt-dlp.");
        Assert.Equal(firstTempDir, secondTempDir);
        Assert.Equal(firstOutputTemplate, secondOutputTemplate);
        Assert.False(Directory.Exists(firstTempDir), "Temp folder should be cleaned after resumed VOD completes.");
    }

    [Fact]
    public async Task LivestreamModeA_PauseIsDisabled_WhileStopAndSaveWorksCleanly()
    {
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

            // FFmpeg remux to mp4 must happen inside jobTempDir
            Assert.StartsWith(_paths.TempDownloadsDirectory, req.Arguments[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(_outDir));
            await File.WriteAllBytesAsync(req.Arguments[^1], new byte[4096], CancellationToken.None);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        await using var stack = await CreateStackAsync(runner);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("video-720-std", "720p", false, 720, 60, "H.264", null, "b[height<=720]/b", null, false, ["mkv", "mp4"]);
        var req = new DownloadJobRequest(
            "live-a", "live1234567", "https://www.youtube.com/watch?v=live1234567", "Live Mode A", "Chan", null, _outDir,
            q, "mp4", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromNow);

        await stack.Orchestrator.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
                doneTcs.TrySetResult(s);
        }));

        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Pause MUST be explicitly disabled on active livestreams
        Assert.False(await stack.Orchestrator.PauseJobAsync("live-a"));

        // Stop & Save succeeds cleanly
        stack.Orchestrator.RequestCancel("live-a");

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
        var runner = new DelegateProcessRunner((_, _) => throw new InvalidOperationException("yt-dlp must not launch when DVR is disabled."));
        await using var stack = await CreateStackAsync(runner);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("best", "Best", false, 1080, 60, "H.264", null, "bv*+ba/b", null, true, ["mkv"]);
        var req = new DownloadJobRequest(
            "live-nodvr", "live1234567", "https://www.youtube.com/watch?v=live1234567", "No DVR Stream", "Chan", null, _outDir,
            q, "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromStartUntilEnd, IsDvrAvailable: false);

        await stack.Orchestrator.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
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

        await using var stack = await CreateStackAsync(runner);

        var doneTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new QualityOption("video-360", "360p", false, 360, 30, "H.264", null, "134+140", null, true, ["mkv"]);
        var req = new DownloadJobRequest(
            "live-fallback", "live1234567", "https://www.youtube.com/watch?v=live1234567", "Fallback Stream", "Chan", null, _outDir,
            q, "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromStartToLiveEdge,
            LiveEdgeMarginSeconds: 30, LivestreamStartUtc: DateTimeOffset.UtcNow.AddMinutes(-10), IsDvrAvailable: true);

        await stack.Orchestrator.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
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
    public async Task PruneOrphanedTempDirectories_ProtectsPausedAndRecoverableJobs_DeletesOnlyOrphans()
    {
        var runner = new DelegateProcessRunner((_, _) => Task.FromResult(new ProcessRunResult(0, false, false, false, TimeSpan.Zero, "", "")));
        await using var stack = await CreateStackAsync(runner);

        var pausedTemp = Path.Combine(_paths.TempDownloadsDirectory, "paused-vod-1");
        var liveTemp = Path.Combine(_paths.TempDownloadsDirectory, "interrupted-live-2");
        var orphanTemp = Path.Combine(_paths.TempDownloadsDirectory, "stale-crash-orphan-999");

        Directory.CreateDirectory(pausedTemp);
        Directory.CreateDirectory(liveTemp);
        Directory.CreateDirectory(orphanTemp);
        await File.WriteAllBytesAsync(Path.Combine(pausedTemp, "video.f137.mp4.part"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(liveTemp, "stream.mkv.part"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(orphanTemp, "garbage.part"), new byte[128]);

        var pausedJob = PersistedDownloadJob.FromRequest(
            new DownloadJobRequest("paused-vod-1", "v1", "https://www.youtube.com/watch?v=v1", "Paused VOD", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
            pausedTemp) with { Status = DownloadJobState.Paused };
        var liveJob = PersistedDownloadJob.FromRequest(
            new DownloadJobRequest("interrupted-live-2", "v2", "https://www.youtube.com/watch?v=v2", "Live", "C", null, _outDir, MakeVodQuality(), "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromNow),
            liveTemp) with { Status = DownloadJobState.InterruptedLivestream };

        await stack.Jobs.InsertQueuedJobAsync(pausedJob);
        await stack.Jobs.InsertQueuedJobAsync(liveJob);

        var pruned = await stack.Orchestrator.PruneOrphanedTempDirectoriesAsync();

        Assert.Equal(1, pruned);
        Assert.True(Directory.Exists(pausedTemp), "Paused VOD temp folder must NEVER be pruned.");
        Assert.True(Directory.Exists(liveTemp), "Interrupted livestream temp folder must NEVER be pruned.");
        Assert.False(Directory.Exists(orphanTemp), "Truly orphaned temp folder must be deleted.");
    }

    [Fact]
    public async Task StartupCrashRecovery_RestoresInterruptedVodAsPaused_AndRecoversSplitTrackLivestream()
    {
        var vodTemp = Path.Combine(_paths.TempDownloadsDirectory, "crash-vod");
        var liveTemp = Path.Combine(_paths.TempDownloadsDirectory, "crash-live");
        Directory.CreateDirectory(vodTemp);
        Directory.CreateDirectory(liveTemp);
        await File.WriteAllBytesAsync(Path.Combine(vodTemp, "Crash VOD [vid1].f137.mp4.part"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(liveTemp, "Crash Live [vid2].f134.mp4.part"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(liveTemp, "Crash Live [vid2].f140.m4a.part"), new byte[4096]);

        var muxInvoked = false;
        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            if (req.ExecutablePath.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && req.Arguments.Contains("-shortest"))
            {
                muxInvoked = true;
                await File.WriteAllBytesAsync(req.Arguments[^1], new byte[12_288]);
                return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
            }

            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, string.Empty);
        });

        // Pre-populate SQLite as if the app crashed mid-download
        using (var seedDb = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance))
        {
            await seedDb.InitializeAsync();
            var seedRepo = new SqliteDownloadJobRepository(seedDb, NullLogger<SqliteDownloadJobRepository>.Instance);
            await seedRepo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
                new DownloadJobRequest("crash-vod", "vid1", "https://www.youtube.com/watch?v=vid1", "Crash VOD", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
                vodTemp, "Crash VOD [vid1]") with { Status = DownloadJobState.Downloading });
            await seedRepo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
                new DownloadJobRequest("crash-live", "vid2", "https://www.youtube.com/watch?v=vid2", "Crash Live", "C", null, _outDir, MakeVodQuality(), "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromStartToLiveEdge),
                liveTemp, "Crash Live [vid2]") with { Status = DownloadJobState.RecordingLive });
        }

        await using var stack = await CreateStackAsync(runner);
        var recovered = await stack.Orchestrator.InitializeAndRecoverAsync();

        var vod = Assert.Single(recovered, j => j.JobId == "crash-vod");
        Assert.Equal(DownloadJobState.Paused, vod.Status);

        var live = Assert.Single(recovered, j => j.JobId == "crash-live");
        Assert.Equal(DownloadJobState.InterruptedLivestream, live.Status);

        // Now recover the split-track partial livestream!
        var snap = await stack.Orchestrator.RecoverPartialLivestreamAsync("crash-live");
        Assert.Equal(DownloadJobState.Completed, snap.State);
        Assert.True(muxInvoked, "Split-track recovery must invoke 2-input ffmpeg -c copy -shortest.");
        Assert.NotNull(snap.OutputFilePath);
        Assert.True(File.Exists(snap.OutputFilePath));
        Assert.False(Directory.Exists(liveTemp));
        Assert.Single(await stack.History.LoadAsync());
    }

    [Fact]
    public async Task QueueOrderPersistsAcrossRestart_AndExecutesInReorderedSequence()
    {
        // Seed 3 queued jobs in SQLite and reorder BEFORE creating the orchestrator that runs them
        using (var seedDb = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance))
        {
            await seedDb.InitializeAsync();
            var repo = new SqliteDownloadJobRepository(seedDb, NullLogger<SqliteDownloadJobRepository>.Instance);
            await repo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
                new DownloadJobRequest("q-1", "v1", "https://www.youtube.com/watch?v=v1", "One", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
                Path.Combine(_paths.TempDownloadsDirectory, "q-1")));
            await repo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
                new DownloadJobRequest("q-2", "v2", "https://www.youtube.com/watch?v=v2", "Two", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
                Path.Combine(_paths.TempDownloadsDirectory, "q-2")));
            await repo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
                new DownloadJobRequest("q-3", "v3", "https://www.youtube.com/watch?v=v3", "Three", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
                Path.Combine(_paths.TempDownloadsDirectory, "q-3")));

            // Move q-3 to top -> [q-3, q-1, q-2], then move q-2 up -> [q-3, q-2, q-1]
            await repo.MoveQueuedJobToTopAsync("q-3");
            await repo.MoveQueuedJobUpAsync("q-2");
        }

        var executionOrder = new ConcurrentQueue<string>();
        var completedCount = 0;
        var allDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            var url = req.Arguments[^1];
            executionOrder.Enqueue(url[^2..]); // v3, v2, v1
            var argList = req.Arguments.ToList();
            var homeDir = ExtractPrefixedArg(argList, "home:");
            var outTemplate = argList[argList.IndexOf("-o") + 1];
            var mp4Path = Path.Combine(homeDir, outTemplate.Replace(".%(ext)s", ".mp4", StringComparison.Ordinal));
            await File.WriteAllBytesAsync(mp4Path, new byte[1024]);
            req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, string.Empty);
        });

        // Use concurrency=1 so execution order is deterministic!
        await using var stack = await CreateStackAsync(runner, maxConcurrent: 1);
        stack.Orchestrator.JobProgressChanged += snap =>
        {
            if (snap.State == DownloadJobState.Completed && Interlocked.Increment(ref completedCount) == 3)
                allDoneTcs.TrySetResult();
        };

        await stack.Orchestrator.InitializeAndRecoverAsync();
        await allDoneTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["v3", "v2", "v1"], executionOrder.ToArray());
    }

    [Fact]
    public async Task SmartRetry_TransientBackoffRetriesAndAbortsImmediatelyOnCancel()
    {
        var attempts = 0;
        var enteredDelayTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCancelled = false;

        var runner = new DelegateProcessRunner((_, _) =>
        {
            attempts++;
            return Task.FromResult(new ProcessRunResult(1, false, false, false, TimeSpan.FromMilliseconds(10),
                string.Empty, "ERROR: Unable to download webpage: HTTP Error 503: Service Unavailable"));
        });

        await using var stack = await CreateStackAsync(runner, retryDelayAsync: async (delay, ct) =>
        {
            enteredDelayTcs.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                delayCancelled = true;
                throw;
            }
        });

        var cancelledTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new DownloadJobRequest("retry-cancel", "v99", "https://www.youtube.com/watch?v=v99", "Retry Cancel", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive);
        await stack.Orchestrator.EnqueueAsync(req, new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            if (s.State == DownloadJobState.Cancelled) cancelledTcs.TrySetResult(s);
        }));

        await enteredDelayTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stack.Orchestrator.RequestCancel("retry-cancel"));

        var final = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DownloadJobState.Cancelled, final.State);
        Assert.True(delayCancelled);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SmartRetry_Http403TriggersAutoReprobeAndSucceeds_WhilePermanentFailsFastAndRecordsDiagnostic()
    {
        var probeCalls = 0;
        var fakeProbe = new StubMediaProbeService(url =>
        {
            probeCalls++;
            return new MediaProbeResult(
                "v403", url, "Refreshed Video", "Chan", 120, null, MediaLiveStatus.NotLive, null,
                [MakeVodQuality()], [], null, []);
        });

        var run403Count = 0;
        var permCount = 0;

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            var url = req.Arguments[^1];
            if (url.Contains("v403"))
            {
                run403Count++;
                if (run403Count == 1)
                    return new ProcessRunResult(1, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, "ERROR: HTTP Error 403: Forbidden");

                var argList = req.Arguments.ToList();
                var homeDir = ExtractPrefixedArg(argList, "home:");
                var outTemplate = argList[argList.IndexOf("-o") + 1];
                var mp4Path = Path.Combine(homeDir, outTemplate.Replace(".%(ext)s", ".mp4", StringComparison.Ordinal));
                await File.WriteAllBytesAsync(mp4Path, new byte[4096]);
                req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");
                return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, string.Empty);
            }

            permCount++;
            return new ProcessRunResult(1, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, "ERROR: Private video. Cookie: SID=secret123");
        });

        await using var stack = await CreateStackAsync(runner, probeService: fakeProbe, retryDelayAsync: (_, _) => Task.CompletedTask);

        // 1. HTTP 403 -> auto re-probe -> succeeds on attempt 2
        var done403 = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await stack.Orchestrator.EnqueueAsync(
            new DownloadJobRequest("job-403", "v403", "https://www.youtube.com/watch?v=v403", "403 Video", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
            new SyncProgress<DownloadProgressSnapshot>(s => { if (s.State is DownloadJobState.Completed or DownloadJobState.Failed) done403.TrySetResult(s); }));

        var res403 = await done403.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Completed, res403.State);
        Assert.Equal(1, probeCalls);
        Assert.Equal(2, run403Count);

        // 2. Permanent error -> 0 retries, recorded redacted in DiagnosticEvents
        var donePerm = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await stack.Orchestrator.EnqueueAsync(
            new DownloadJobRequest("job-perm", "vpriv", "https://www.youtube.com/watch?v=vpriv", "Private Clip", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive),
            new SyncProgress<DownloadProgressSnapshot>(s => { if (s.State is DownloadJobState.Completed or DownloadJobState.Failed) donePerm.TrySetResult(s); }));

        var resPerm = await donePerm.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadJobState.Failed, resPerm.State);
        Assert.Equal(1, permCount);

        var diagEvents = await stack.Diagnostics.GetRecentFailuresAsync(25);
        Assert.Single(diagEvents);
        Assert.Equal("Permanent", diagEvents[0].ErrorCategory);
        Assert.DoesNotContain("secret123", diagEvents[0].RedactedTechnicalDetails);
    }

    [Fact]
    public async Task LiveConcurrencyIncrease_StartsWaitingQueuedJobImmediatelyWithoutDroppingOrDuplicating()
    {
        var job1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJob1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runIds = new ConcurrentBag<string>();
        var doneCount = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new DelegateProcessRunner(async (req, _) =>
        {
            var url = req.Arguments[^1];
            runIds.Add(url);

            if (url.EndsWith("j1"))
            {
                job1Started.TrySetResult();
                await releaseJob1.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else if (url.EndsWith("j2"))
            {
                job2Started.TrySetResult();
            }

            var argList = req.Arguments.ToList();
            var homeDir = ExtractPrefixedArg(argList, "home:");
            var outTemplate = argList[argList.IndexOf("-o") + 1];
            var mp4Path = Path.Combine(homeDir, outTemplate.Replace(".%(ext)s", ".mp4", StringComparison.Ordinal));
            await File.WriteAllBytesAsync(mp4Path, new byte[1024]);
            req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mp4Path}");
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), string.Empty, string.Empty);
        });

        await using var stack = await CreateStackAsync(runner, maxConcurrent: 1);
        stack.Orchestrator.JobProgressChanged += s =>
        {
            if (s.State == DownloadJobState.Completed && Interlocked.Increment(ref doneCount) == 3)
                allDone.TrySetResult();
        };

        await stack.Orchestrator.EnqueueAsync(new DownloadJobRequest("j1", "j1", "https://www.youtube.com/watch?v=j1", "J1", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive));
        await stack.Orchestrator.EnqueueAsync(new DownloadJobRequest("j2", "j2", "https://www.youtube.com/watch?v=j2", "J2", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive));
        await stack.Orchestrator.EnqueueAsync(new DownloadJobRequest("j3", "j3", "https://www.youtube.com/watch?v=j3", "J3", "C", null, _outDir, MakeVodQuality(), "mp4", MediaLiveStatus.NotLive));

        await job1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(job2Started.Task.IsCompleted);

        // Increase live concurrency from 1 -> 2: job 2 must start IMMEDIATELY while job 1 is still blocked!
        stack.Orchestrator.UpdateMaxConcurrency(2);
        await job2Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseJob1.TrySetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(3, runIds.Count);
        Assert.Equal(3, runIds.Distinct().Count());
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
            services.AddSingleton<ISqliteDatabaseManager, SqliteDatabaseManager>();
            services.AddSingleton<IDownloadJobRepository, SqliteDownloadJobRepository>();
            services.AddSingleton<IDiagnosticsRepository, SqliteDiagnosticsRepository>();
            services.AddSingleton<IHistoryStore, SqliteHistoryStore>();
            services.AddSingleton<IDiskSpaceReservationLedger>(_ => new DiskSpaceReservationLedger());
            services.AddSingleton<IMediaIntegrityService, MediaIntegrityService>();
            services.AddSingleton<IToolManager, ToolManager>();
            services.AddSingleton<OutputPathGuard>();
            services.AddSingleton<IMediaProbeService, MediaProbeService>();
            services.AddSingleton<IDownloadOrchestrator, DownloadOrchestrator>();
            return services.BuildServiceProvider();
        }

        await using (var asyncProvider = BuildRealProvider())
        {
            var orch1 = asyncProvider.GetRequiredService<IDownloadOrchestrator>();
            Assert.NotNull(orch1);
            await asyncProvider.DisposeAsync();
        }

        using (var syncProvider = BuildRealProvider())
        {
            var orch2 = syncProvider.GetRequiredService<IDownloadOrchestrator>();
            Assert.NotNull(orch2);
            var ex = Record.Exception(() => syncProvider.Dispose());
            Assert.Null(ex);
        }

        using var provider3 = BuildRealProvider();
        var orch3 = provider3.GetRequiredService<IDownloadOrchestrator>();
        await orch3.DisposeAsync();
        var doubleDisposeEx = Record.Exception(() => orch3.Dispose());
        Assert.Null(doubleDisposeEx);
    }

    private static QualityOption MakeVodQuality() =>
        new("video-1080-std", "1080p", false, 1080, 30, "H.264", 12_000_000, "bv*[height<=1080]+ba/b", null, true, ["mp4", "mkv"]);

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

    private sealed class StubMediaProbeService(Func<string, MediaProbeResult>? handler = null) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string url, CancellationToken ct = default)
        {
            if (handler is not null)
                return Task.FromResult(handler(url));

            return Task.FromResult(new MediaProbeResult(
                "stub", url, "Stub", "Chan", 120, null, MediaLiveStatus.NotLive, null,
                [MakeVodQuality()], [], null, []));
        }
    }

    private sealed class StubMediaIntegrityService : IMediaIntegrityService
    {
        public Task<MediaIntegrityProbeInfo> ProbeFileAsync(ResolvedToolchain toolchain, string filePath, bool? expectVideo = null, bool expectAudio = true, CancellationToken ct = default)
        {
            var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            return Task.FromResult(new MediaIntegrityProbeInfo(true, null, size, 212.0, true, true, "1920x1080", "h264", "aac"));
        }

        public Task<MediaIntegrityResult> VerifyAndRepairIfNeededAsync(ResolvedToolchain toolchain, string filePath, bool? expectVideo = null, bool expectAudio = true, string? workingTempDirectory = null, CancellationToken ct = default)
        {
            var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            return Task.FromResult(new MediaIntegrityResult(MediaIntegrityStatus.Verified, "Verified healthy via ffprobe.", size, 212.0, "1920x1080", "h264", "aac", false, DateTimeOffset.UtcNow));
        }
    }
}
