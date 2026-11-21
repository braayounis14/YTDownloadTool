using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Livestream;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Livestream;

public sealed class ModeCLiveEdgeWatchdogTests : IDisposable
{
    private readonly string _sandboxRoot;

    public ModeCLiveEdgeWatchdogTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ytdl_live_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ProgressParser_ParsesMultiStreamPrefixedLinesFromLiveFromStart()
    {
        const string line1 = "1: YTDL_PROG|downloading|204800|NA|NA|512000|NA|42|1200|avc1.64002A|none|299";
        const string line2 = "2: YTDL_PROG|downloading|65536|NA|NA|128000|NA|43|1200|none|mp4a.40.2|140";
        const string finishedLine = "1: YTDL_PROG|finished|999999|NA|NA|NA|NA|0|1200|avc1.64002A|none|299";

        var p1 = YtDlpProgressParser.ParseLine(line1);
        Assert.Equal(ParsedOutputKind.DownloadProgress, p1.Kind);
        Assert.Equal("1", p1.StreamChannel);
        Assert.Equal("299", p1.FormatId);
        Assert.Equal(42, p1.FragmentIndex);
        Assert.Equal(1200, p1.FragmentCount);
        Assert.True(p1.HasVideo);
        Assert.False(p1.HasAudio);

        var p2 = YtDlpProgressParser.ParseLine(line2);
        Assert.Equal("2", p2.StreamChannel);
        Assert.Equal("140", p2.FormatId);
        Assert.Equal(43, p2.FragmentIndex);
        Assert.False(p2.HasVideo);
        Assert.True(p2.HasAudio);

        var pFin = YtDlpProgressParser.ParseLine(finishedLine);
        Assert.Null(pFin.FragmentIndex); // Must ignore fragment_index on 'finished' lines
    }

    [Fact]
    public void Watchdog_EstimatesFragmentDurationAndStopsOnlyWhenBothTracksReachTarget()
    {
        // Stream started 1 hour ago (3600s), 720 fragments -> ~5.0s per fragment.
        // Live-edge margin 30s -> 6 margin fragments -> target stop fragment = 714.
        var now = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
        var start = now.AddHours(-1);
        var watchdog = new ModeCLiveEdgeWatchdog(start, liveEdgeMarginSeconds: 30, isDvrWindowCapped: false, expectedTrackCount: 2);

        // Track 1 (video) reaches 715 first, track 2 (audio) hasn't started yet -> MUST NOT STOP!
        var d1 = watchdog.ObserveFragmentProgress("299", 715, 720, now);
        Assert.False(d1.ShouldStopNow);
        Assert.Equal(714, watchdog.TargetStopFragment);
        Assert.Equal(5.0, watchdog.EstimatedFragmentDurationSeconds, 1);

        // Track 2 (audio) at 700 -> still behind target 714 -> MUST NOT STOP!
        var d2 = watchdog.ObserveFragmentProgress("140", 700, 720, now.AddSeconds(1));
        Assert.False(d2.ShouldStopNow);

        // Track 2 (audio) reaches 714 -> BOTH tracks >= 714 -> MUST STOP NOW!
        var d3 = watchdog.ObserveFragmentProgress("140", 714, 720, now.AddSeconds(2));
        Assert.True(d3.ShouldStopNow);
        Assert.True(d3.HasReachedLiveEdge);
        Assert.Equal(100, d3.CatchUpProgressPercent);
    }

    [Fact]
    public void Watchdog_24x7CappedStream_ClampsTargetTo120HourDvrWindow()
    {
        // Empirical Sky News scenario: running 23.56 days (2,036,000s), absolute head = 408,411 frags (~4.985s/frag).
        // 120h window = 432,000s / 4.985s ≈ 86,659 frags; minus 30s margin (7 frags) = ~86,652.
        var now = new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero);
        var start = now.AddSeconds(-2_036_000);

        var fragDur = ModeCLiveEdgeWatchdog.EstimateFragmentDurationSeconds(start, 408_411, now);
        Assert.InRange(fragDur, 4.9, 5.1);

        var target = ModeCLiveEdgeWatchdog.ComputeTargetStopFragment(408_411, fragDur, 30, isDvrWindowCapped: true);
        Assert.InRange(target, 86_000, 87_000);
    }

    [Fact]
    public void Watchdog_LiveHeadWaitFallback_StopsCleanlyOnceBothTracksActive()
    {
        var now = DateTimeOffset.UtcNow;
        var watchdog = new ModeCLiveEdgeWatchdog(null, 30, false, expectedTrackCount: 2);

        watchdog.ObserveFragmentProgress("134", 10, 500, now);
        watchdog.ObserveFragmentProgress("140", 10, 500, now);

        var waitDecision = watchdog.ObserveLiveHeadWaitSignal();
        Assert.True(waitDecision.ShouldStopNow);
    }

    [Fact]
    public async Task DownloadOrchestrator_ModeC_StopsViaWatchdog_AndRemuxesToMp4AndPersistsHistory()
    {
        var paths = new DefaultAppPaths(
            Path.Combine(_sandboxRoot, "app"),
            Path.Combine(_sandboxRoot, "local"),
            Path.Combine(_sandboxRoot, "roaming"),
            Path.Combine(_sandboxRoot, "videos"));
        paths.EnsureDirectoriesExist();

        var outDir = Path.Combine(_sandboxRoot, "downloads");
        Directory.CreateDirectory(outDir);

        var settingsStore = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        var historyStore = new HistoryStore(paths, NullLogger<HistoryStore>.Instance);

        var fakeToolchain = new ResolvedToolchain(
            @"C:\fake\yt-dlp.exe", @"C:\fake", @"C:\fake\ffmpeg.exe", @"C:\fake\ffprobe.exe", @"C:\fake\deno.exe",
            "2026.10.26", "8.1", "2.9.8");

        var fakeToolManager = new StubToolManager(fakeToolchain);
        var ffmpegCalledForRemux = false;

        var fakeRunner = new DelegateProcessRunner(async (req, ct) =>
        {
            if (req.ExecutablePath.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
            {
                var argList = req.Arguments.ToList();
                var tempPathArg = argList.First(a => a.StartsWith("temp:", StringComparison.Ordinal))["temp:".Length..];
                var homePathArg = argList.First(a => a.StartsWith("home:", StringComparison.Ordinal))["home:".Length..];
                var outTemplate = argList[argList.IndexOf("-o") + 1];
                Assert.False(Path.IsPathRooted(outTemplate), "-o must be relative so yt-dlp honors -P temp/home.");

                var relBase = outTemplate.Replace(".%(ext)s", string.Empty, StringComparison.Ordinal);
                var mkvPath = Path.Combine(homePathArg, relBase + ".mkv");

                // Save folder MUST remain empty during staging!
                Assert.Empty(Directory.GetFiles(outDir));
                await File.WriteAllBytesAsync(Path.Combine(tempPathArg, relBase + ".f134.mp4.part-Frag1"), new byte[128], CancellationToken.None);

                // Simulate 20-fragment stream (started 100s ago -> ~5s/frag, margin 30s -> target = 14)
                req.OnStandardOutput?.Invoke("1: YTDL_PROG|downloading|100000|NA|NA|500000|NA|10|20|avc1|none|134");
                req.OnStandardOutput?.Invoke("2: YTDL_PROG|downloading|40000|NA|NA|100000|NA|10|20|none|mp4a|140");
                req.OnStandardOutput?.Invoke("1: YTDL_PROG|downloading|200000|NA|NA|500000|NA|14|20|avc1|none|134");
                req.OnStandardOutput?.Invoke("2: YTDL_PROG|downloading|80000|NA|NA|100000|NA|14|20|none|mp4a|140");

                // Mode C watchdog should cancel ct immediately when both tracks hit 14
                Assert.True(ct.IsCancellationRequested, "Expected ModeCLiveEdgeWatchdog to request graceful stop.");

                // Simulate yt-dlp catching CTRL_C, merging MKV into temp/home, and printing YTDL_FINAL_FILE
                await File.WriteAllBytesAsync(mkvPath, new byte[4096], CancellationToken.None);
                req.OnStandardOutput?.Invoke($"YTDL_FINAL_FILE:{mkvPath}");

                return new ProcessRunResult(0, WasCancelled: true, GracefulStopUsed: true, ForceKilled: false, TimeSpan.FromSeconds(1), string.Empty, string.Empty);
            }

            // Simulate FFmpeg MKV -> MP4 faststart remux inside job temp dir
            ffmpegCalledForRemux = true;
            var mp4Out = req.Arguments[^1];
            Assert.StartsWith(paths.TempDownloadsDirectory, mp4Out, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(mp4Out, new byte[4096], CancellationToken.None);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(50), string.Empty, string.Empty);
        });

        using var db = new SqliteDatabaseManager(paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();
        var jobRepo = new SqliteDownloadJobRepository(db, NullLogger<SqliteDownloadJobRepository>.Instance);
        var diagRepo = new SqliteDiagnosticsRepository(db, NullLogger<SqliteDiagnosticsRepository>.Instance);
        var ledger = new DiskSpaceReservationLedger(_ => 500L * 1024 * 1024 * 1024);

        await using var orchestrator = new DownloadOrchestrator(
            paths,
            fakeToolManager,
            fakeRunner,
            settingsStore,
            historyStore,
            new OutputPathGuard(),
            jobRepo,
            diagRepo,
            ledger,
            new StubProbeService(),
            new StubIntegrityService(),
            NullLogger<DownloadOrchestrator>.Instance);

        var snapshots = new List<DownloadProgressSnapshot>();
        var completedTcs = new TaskCompletionSource<DownloadProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress<DownloadProgressSnapshot>(s =>
        {
            snapshots.Add(s);
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
                completedTcs.TrySetResult(s);
        });

        var quality = new QualityOption("video-360-std", "360p", false, 360, 30, "H.264", null, "134+140", null, true, ["mkv", "mp4"]);
        var jobReq = new DownloadJobRequest(
            JobId: "live-mode-c-1",
            VideoId: "53SxFKpbKDE",
            SourceUrl: "https://www.youtube.com/watch?v=53SxFKpbKDE",
            Title: "Live News Stream",
            Channel: "News",
            ThumbnailUrl: null,
            OutputDirectory: outDir,
            SelectedQuality: quality,
            OutputContainer: "mp4",
            LiveStatus: MediaLiveStatus.IsLive,
            LivestreamMode: LivestreamRecordingMode.RecordFromStartToLiveEdge,
            LiveEdgeMarginSeconds: 30,
            LivestreamStartUtc: DateTimeOffset.UtcNow.AddSeconds(-100),
            IsDvrAvailable: true);

        await orchestrator.EnqueueAsync(jobReq, progress);
        var final = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DownloadJobState.Completed, final.State);
        Assert.True(ffmpegCalledForRemux);
        Assert.NotNull(final.OutputFilePath);
        Assert.StartsWith(outDir, final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".mp4", final.OutputFilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(final.OutputFilePath));
        // Only the final MP4 exists in Save folder and jobTempDir is deleted cleanly
        Assert.Single(Directory.GetFiles(outDir));
        Assert.False(Directory.Exists(Path.Combine(paths.TempDownloadsDirectory, "live-mode-c-1")));

        var history = await historyStore.LoadAsync();
        Assert.Single(history);
        Assert.True(history[0].WasLivestream);
        Assert.Equal(LivestreamRecordingMode.RecordFromStartToLiveEdge, history[0].LivestreamMode);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class DelegateProcessRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) => handler(request, ct);
        public void Dispose() { }
    }

    private sealed class StubToolManager(ResolvedToolchain toolchain) : IToolManager
    {
        public Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default) => Task.FromResult(toolchain);
        public Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolStatusInfo>>([]);
        public Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubProbeService : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string url, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubIntegrityService : IMediaIntegrityService
    {
        public Task<MediaIntegrityProbeInfo> ProbeFileAsync(ResolvedToolchain toolchain, string filePath, bool? expectVideo = null, bool expectAudio = true, CancellationToken ct = default)
            => Task.FromResult(new MediaIntegrityProbeInfo(true, null, File.Exists(filePath) ? new FileInfo(filePath).Length : 0, 70.0, true, true, "640x360", "h264", "aac"));

        public Task<MediaIntegrityResult> VerifyAndRepairIfNeededAsync(ResolvedToolchain toolchain, string filePath, bool? expectVideo = null, bool expectAudio = true, string? workingTempDirectory = null, CancellationToken ct = default)
            => Task.FromResult(new MediaIntegrityResult(MediaIntegrityStatus.Verified, "Verified", File.Exists(filePath) ? new FileInfo(filePath).Length : 0, 70.0, "640x360", "h264", "aac", false, DateTimeOffset.UtcNow));
    }
}
