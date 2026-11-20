using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public interface IDownloadOrchestrator : IAsyncDisposable
{
    int ActiveJobCount { get; }
    Task<string> EnqueueAsync(DownloadJobRequest request, IProgress<DownloadProgressSnapshot>? progress = null, CancellationToken ct = default);
    bool RequestCancel(string jobId);
    Task CancelAllAsync(TimeSpan? timeout = null);
}

public sealed class DownloadOrchestrator : IDownloadOrchestrator
{
    private readonly IToolManager _toolManager;
    private readonly IProcessRunner _processRunner;
    private readonly ISettingsStore _settingsStore;
    private readonly IHistoryStore _historyStore;
    private readonly OutputPathGuard _outputPathGuard;
    private readonly ILogger<DownloadOrchestrator> _logger;

    private readonly Channel<QueuedJob> _queue;
    private readonly ConcurrentDictionary<string, JobRuntimeState> _activeJobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly Task _dispatcherTask;
    private int _disposed;

    public DownloadOrchestrator(
        IToolManager toolManager,
        IProcessRunner processRunner,
        ISettingsStore settingsStore,
        IHistoryStore historyStore,
        OutputPathGuard outputPathGuard,
        ILogger<DownloadOrchestrator> logger)
    {
        _toolManager = toolManager;
        _processRunner = processRunner;
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _outputPathGuard = outputPathGuard;
        _logger = logger;

        var maxConcurrent = Math.Clamp(_settingsStore.Current.MaxConcurrentDownloads, 1, 4);
        _concurrencyGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _queue = Channel.CreateUnbounded<QueuedJob>(new UnboundedChannelOptions { SingleReader = true });
        _dispatcherTask = Task.Run(DispatchLoopAsync);
    }

    public int ActiveJobCount => _activeJobs.Count;

    public async Task<string> EnqueueAsync(DownloadJobRequest request, IProgress<DownloadProgressSnapshot>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var jobId = string.IsNullOrWhiteSpace(request.JobId) ? Guid.NewGuid().ToString("N") : request.JobId;
        var normalizedReq = request with { JobId = jobId };

        var runtime = new JobRuntimeState(normalizedReq, progress, CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token));
        if (!_activeJobs.TryAdd(jobId, runtime))
            throw new InvalidOperationException($"A download job with ID '{jobId}' is already active.");

        Report(runtime, new DownloadProgressSnapshot(
            JobId: jobId,
            State: DownloadJobState.Queued,
            Percentage: 0,
            IsIndeterminate: false,
            DownloadedBytes: null,
            TotalBytes: request.SelectedQuality.EstimatedSizeBytes,
            SpeedBytesPerSecond: null,
            Eta: null,
            RecordedDuration: null,
            PhaseLabel: "Queued..."));

        await _queue.Writer.WriteAsync(new QueuedJob(runtime), ct);
        return jobId;
    }

    public bool RequestCancel(string jobId)
    {
        if (!_activeJobs.TryGetValue(jobId, out var runtime))
            return false;

        runtime.UserCancelled = true;
        try
        {
            runtime.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public async Task CancelAllAsync(TimeSpan? timeout = null)
    {
        foreach (var (_, runtime) in _activeJobs)
        {
            runtime.UserCancelled = true;
            try { runtime.Cts.Cancel(); } catch { }
        }

        var waitTasks = _activeJobs.Values
            .Select(r => r.Completion.Task)
            .ToArray();

        if (waitTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(waitTasks).WaitAsync(timeout ?? TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for all active downloads to finalize on CancelAllAsync.");
            }
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(_shutdownCts.Token))
            {
                if (queued.Runtime.Cts.IsCancellationRequested)
                {
                    MarkCancelledAndCleanup(queued.Runtime, outputFile: null);
                    continue;
                }

                await _concurrencyGate.WaitAsync(_shutdownCts.Token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteJobAsync(queued.Runtime);
                    }
                    finally
                    {
                        _concurrencyGate.Release();
                        queued.Runtime.Completion.TrySetResult();
                        _activeJobs.TryRemove(queued.Runtime.Request.JobId, out _);
                        queued.Runtime.Cts.Dispose();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ExecuteJobAsync(JobRuntimeState runtime)
    {
        var req = runtime.Request;
        var ct = runtime.Cts.Token;

        try
        {
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Preparing, 0, true, null, req.SelectedQuality.EstimatedSizeBytes,
                null, null, null, "Preparing engine & destination..."));

            var toolchain = await _toolManager.EnsureToolsReadyAsync(ct: ct);
            OutputPathGuard.EnsureSufficientDiskSpace(req.OutputDirectory, req.SelectedQuality.EstimatedSizeBytes);

            var targetExt = req.SelectedQuality.IsAudioOnly
                ? (req.SelectedQuality.PreferredAudioExtension ?? req.OutputContainer)
                : req.OutputContainer;

            var isActiveLivestream = req.LiveStatus == MediaLiveStatus.IsLive;
            var suffixTag = isActiveLivestream ? $"LIVE {DateTime.Now:yyyy-MM-dd HH-mm}" : null;

            using var reservation = _outputPathGuard.ReserveUniqueOutputPath(
                req.OutputDirectory, req.Title, req.VideoId, targetExt, suffixTag);

            if (isActiveLivestream)
            {
                await ExecuteLivestreamJobAsync(runtime, toolchain, reservation, ct);
            }
            else
            {
                await ExecuteStandardVideoJobAsync(runtime, toolchain, reservation, ct);
            }
        }
        catch (OperationCanceledException)
        {
            MarkCancelledAndCleanup(runtime, outputFile: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download job {JobId} failed.", req.JobId);
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                "Failed", ErrorMessage: ex.Message, TechnicalDetails: ex.ToString()));
        }
    }

    private async Task ExecuteStandardVideoJobAsync(
        JobRuntimeState runtime,
        ResolvedToolchain toolchain,
        OutputPathReservation reservation,
        CancellationToken ct)
    {
        var req = runtime.Request;
        var args = YtDlpArgumentBuilder.BuildStandardDownloadArguments(toolchain, req, reservation.ReservedBasePath);

        var streamIndex = 0;
        var lastStreamFinished = false;
        var expectsTwoStreams = !req.SelectedQuality.IsAudioOnly && req.SelectedQuality.RequiresFFmpeg;
        string? detectedFinalFile = null;
        string? lastDestination = null;
        string? latestNotice = null;

        var result = await _processRunner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = toolchain.YtDlpPath,
            Arguments = args,
            GracefulStop = GracefulStopStrategy.CtrlC,
            GracefulStopTimeout = TimeSpan.FromSeconds(4),
            OnStandardOutput = line => HandleStandardOutput(line),
            OnStandardError = line => HandleStandardOutput(line)
        }, ct);

        if (result.WasCancelled || runtime.UserCancelled)
        {
            CleanupPartialFiles(reservation.ReservedBasePath);
            MarkCancelledAndCleanup(runtime, outputFile: null);
            return;
        }

        if (result.ExitCode != 0)
        {
            var classified = YtDlpErrorClassifier.Classify(result.StandardError, result.ExitCode);
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                "Failed", ErrorMessage: classified.UserMessage, TechnicalDetails: classified.TechnicalSummary));
            return;
        }

        var finalFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation);
        var finalSize = finalFile is not null && File.Exists(finalFile) ? new FileInfo(finalFile).Length : 0;

        await PersistHistorySafeAsync(req, finalFile, finalSize);

        Report(runtime, new DownloadProgressSnapshot(
            req.JobId, DownloadJobState.Completed, 100, false, finalSize > 0 ? finalSize : null,
            finalSize > 0 ? finalSize : null, null, TimeSpan.Zero,
            req.KnownDurationSeconds is > 0 ? TimeSpan.FromSeconds(req.KnownDurationSeconds.Value) : null,
            "Completed", OutputFilePath: finalFile, NonFatalNotice: latestNotice));

        void HandleStandardOutput(string line)
        {
            var parsed = YtDlpProgressParser.ParseLine(line);
            switch (parsed.Kind)
            {
                case ParsedOutputKind.FinalFilePath when !string.IsNullOrWhiteSpace(parsed.FilePath):
                    detectedFinalFile = parsed.FilePath;
                    break;

                case ParsedOutputKind.DestinationPath when !string.IsNullOrWhiteSpace(parsed.FilePath):
                    lastDestination = parsed.FilePath;
                    break;

                case ParsedOutputKind.WarningNotice:
                    latestNotice = parsed.Notice;
                    break;

                case ParsedOutputKind.PostProcess:
                    Report(runtime, new DownloadProgressSnapshot(
                        req.JobId, DownloadJobState.Finalizing, 96, true, null, null, null, null, null,
                        "Merging & finalizing with FFmpeg...", NonFatalNotice: latestNotice));
                    break;

                case ParsedOutputKind.DownloadProgress:
                    if (string.Equals(parsed.Status, "finished", StringComparison.OrdinalIgnoreCase))
                    {
                        lastStreamFinished = true;
                        break;
                    }

                    if (lastStreamFinished)
                    {
                        streamIndex++;
                        lastStreamFinished = false;
                    }

                    double streamPct = 0;
                    if (parsed.DownloadedBytes is > 0 && parsed.TotalBytes is > 0)
                    {
                        streamPct = Math.Clamp((double)parsed.DownloadedBytes.Value / parsed.TotalBytes.Value * 100.0, 0, 100);
                    }
                    else if (parsed.FragmentIndex is > 0 && parsed.FragmentCount is > 0)
                    {
                        streamPct = Math.Clamp((double)parsed.FragmentIndex.Value / parsed.FragmentCount.Value * 100.0, 0, 100);
                    }

                    var overallPct = expectsTwoStreams
                        ? (streamIndex == 0 ? streamPct * 0.85 : 85.0 + streamPct * 0.10)
                        : streamPct * 0.96;

                    var phase = req.SelectedQuality.IsAudioOnly
                        ? "Downloading audio..."
                        : (parsed.HasVideo && !parsed.HasAudio
                            ? "Downloading video stream..."
                            : (!parsed.HasVideo && parsed.HasAudio ? "Downloading audio stream..." : "Downloading..."));

                    Report(runtime, new DownloadProgressSnapshot(
                        req.JobId, DownloadJobState.Downloading, overallPct,
                        parsed.TotalBytes is null && parsed.FragmentCount is null,
                        parsed.DownloadedBytes, parsed.TotalBytes, parsed.SpeedBytesPerSecond, parsed.Eta,
                        null, phase, NonFatalNotice: latestNotice));
                    break;
            }
        }
    }

    private async Task ExecuteLivestreamJobAsync(
        JobRuntimeState runtime,
        ResolvedToolchain toolchain,
        OutputPathReservation reservation,
        CancellationToken ct)
    {
        var req = runtime.Request;
        var mode = req.LivestreamMode ?? LivestreamRecordingMode.RecordFromNow;

        if (mode != LivestreamRecordingMode.RecordFromNow && !req.IsDvrAvailable)
        {
            throw new InvalidOperationException(
                "This livestream does not expose DVR/rewind history. Please choose 'Record from now' (Mode A).");
        }

        TimeSpan? modeCTargetDuration = null;
        if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge)
        {
            modeCTargetDuration = CalculateModeCTargetDuration(req.LivestreamStartUtc, req.LiveEdgeMarginSeconds);
            if (modeCTargetDuration is null)
            {
                throw new InvalidOperationException(
                    $"This livestream has only been live for less than the configured {req.LiveEdgeMarginSeconds}s live-edge margin. Wait a moment or use Mode A ('Record from now').");
            }
        }

        var args = YtDlpArgumentBuilder.BuildLivestreamArguments(
            toolchain,
            req,
            reservation.ReservedBasePath,
            mode,
            modeCTargetDuration,
            preferMuxedHlsForModeC: req.HasMuxedHlsFromStart);

        using var jobLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var modeCWatchdogTriggered = false;
        string? detectedFinalFile = null;
        string? lastDestination = null;
        string? latestNotice = null;
        TimeSpan? lastRecordedDuration = null;
        long? lastDownloadedBytes = null;

        var initialPhase = mode switch
        {
            LivestreamRecordingMode.RecordFromNow => "Recording live stream...",
            LivestreamRecordingMode.RecordFromStartUntilEnd => "Catching up from stream start...",
            LivestreamRecordingMode.RecordFromStartToLiveEdge => $"Downloading start → {FormatDuration(modeCTargetDuration!.Value)} (stopping {req.LiveEdgeMarginSeconds}s before live)...",
            _ => "Recording live stream..."
        };

        Report(runtime, new DownloadProgressSnapshot(
            req.JobId,
            mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp,
            0, mode != LivestreamRecordingMode.RecordFromStartToLiveEdge,
            null, null, null, null, TimeSpan.Zero, initialPhase));

        var result = await _processRunner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = toolchain.YtDlpPath,
            Arguments = args,
            GracefulStop = GracefulStopStrategy.CtrlC,
            GracefulStopTimeout = TimeSpan.FromSeconds(12),
            OnStandardOutput = line => HandleLiveLine(line),
            OnStandardError = line => HandleLiveLine(line)
        }, jobLinkedCts.Token);

        // Locate whatever crash-safe recording file exists on disk (even after user Stop & Save or Mode C watchdog)
        var capturedFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation);

        // Mode C fallback: if FFmpeg -t failed due to live DASH incompatibility, retry cleanly with native fragment watchdog
        if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge
            && !runtime.UserCancelled
            && !modeCWatchdogTriggered
            && result.ExitCode != 0
            && (capturedFile is null || !File.Exists(capturedFile))
            && result.StandardError.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Mode C bounded FFmpeg capture failed; retrying with native --live-from-start + live-edge watchdog.");
            latestNotice = "Switched to native fragment live-edge capture for this stream.";

            var fallbackArgs = YtDlpArgumentBuilder.BuildLivestreamArguments(
                toolchain, req, reservation.ReservedBasePath, mode, modeCTargetDuration: null, preferMuxedHlsForModeC: false);

            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            result = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.YtDlpPath,
                Arguments = fallbackArgs,
                GracefulStop = GracefulStopStrategy.CtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(12),
                OnStandardOutput = line => HandleLiveLine(line, fallbackCts),
                OnStandardError = line => HandleLiveLine(line, fallbackCts)
            }, fallbackCts.Token);

            capturedFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation);
        }

        // If user clicked Stop & Save (or Mode C watchdog stopped cleanly) and we have a playable file, finalize it!
        var hasSavedRecording = !string.IsNullOrWhiteSpace(capturedFile)
                                && File.Exists(capturedFile)
                                && new FileInfo(capturedFile).Length > 0;

        if (!hasSavedRecording)
        {
            if (runtime.UserCancelled)
            {
                MarkCancelledAndCleanup(runtime, outputFile: null);
                return;
            }

            var classified = YtDlpErrorClassifier.Classify(result.StandardError, result.ExitCode);
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Failed, 0, false, lastDownloadedBytes, null, null, null, lastRecordedDuration,
                "Recording failed", ErrorMessage: classified.UserMessage, TechnicalDetails: classified.TechnicalSummary));
            return;
        }

        // Remux crash-safe MKV/TS -> MP4 with +faststart if user requested MP4
        var finalOutputPath = capturedFile!;
        if (req.OutputContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            && !finalOutputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Finalizing, 98, true, lastDownloadedBytes, null, null, null, lastRecordedDuration,
                "Finalizing MP4 container (faststart)...", NonFatalNotice: latestNotice));

            var targetMp4 = Path.ChangeExtension(finalOutputPath, ".mp4");
            var remuxRes = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.FFmpegPath,
                Arguments = YtDlpArgumentBuilder.BuildRemuxToMp4Arguments(finalOutputPath, targetMp4),
                GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(5)
            }, CancellationToken.None);

            if (remuxRes.ExitCode == 0 && File.Exists(targetMp4) && new FileInfo(targetMp4).Length > 0)
            {
                try { File.Delete(finalOutputPath); } catch { }
                finalOutputPath = targetMp4;
            }
            else
            {
                latestNotice = "Recording saved safely in MKV/TS format (MP4 remux was skipped to preserve stream codecs).";
            }
        }

        var finalSize = new FileInfo(finalOutputPath).Length;
        await PersistHistorySafeAsync(req, finalOutputPath, finalSize);

        var completionLabel = runtime.UserCancelled
            ? "Recording stopped & saved"
            : (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge ? "Historical stream captured" : "Recording completed");

        Report(runtime, new DownloadProgressSnapshot(
            req.JobId, DownloadJobState.Completed, 100, false, finalSize, finalSize, null, TimeSpan.Zero,
            lastRecordedDuration, completionLabel, OutputFilePath: finalOutputPath, NonFatalNotice: latestNotice));

        void HandleLiveLine(string line, CancellationTokenSource? overrideStopCts = null)
        {
            var parsed = YtDlpProgressParser.ParseLine(line);
            var activeStopCts = overrideStopCts ?? jobLinkedCts;

            switch (parsed.Kind)
            {
                case ParsedOutputKind.FinalFilePath when !string.IsNullOrWhiteSpace(parsed.FilePath):
                    detectedFinalFile = parsed.FilePath;
                    break;

                case ParsedOutputKind.DestinationPath when !string.IsNullOrWhiteSpace(parsed.FilePath):
                    lastDestination = parsed.FilePath;
                    break;

                case ParsedOutputKind.WarningNotice:
                    latestNotice = parsed.Notice;
                    break;

                case ParsedOutputKind.FFmpegProgress:
                    lastRecordedDuration = parsed.MediaTime ?? lastRecordedDuration;
                    lastDownloadedBytes = parsed.DownloadedBytes ?? lastDownloadedBytes;

                    var pct = mode == LivestreamRecordingMode.RecordFromStartToLiveEdge && modeCTargetDuration is { TotalSeconds: > 0 } && lastRecordedDuration.HasValue
                        ? Math.Clamp(lastRecordedDuration.Value.TotalSeconds / modeCTargetDuration.Value.TotalSeconds * 96.0, 0, 96)
                        : 0;

                    var state = mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp;
                    var phase = lastRecordedDuration.HasValue
                        ? (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge && modeCTargetDuration.HasValue
                            ? $"Capturing {FormatDuration(lastRecordedDuration.Value)} / {FormatDuration(modeCTargetDuration.Value)}"
                            : $"Recording live ({FormatDuration(lastRecordedDuration.Value)})")
                        : "Recording live...";

                    Report(runtime, new DownloadProgressSnapshot(
                        req.JobId, state, pct, mode != LivestreamRecordingMode.RecordFromStartToLiveEdge,
                        lastDownloadedBytes, null, null, null, lastRecordedDuration, phase, NonFatalNotice: latestNotice));

                    // Secondary safety watchdog for Mode C: if media time crosses target duration, request graceful stop
                    if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge
                        && modeCTargetDuration.HasValue
                        && lastRecordedDuration >= modeCTargetDuration.Value
                        && !modeCWatchdogTriggered)
                    {
                        modeCWatchdogTriggered = true;
                        try { activeStopCts.Cancel(); } catch { }
                    }
                    break;

                case ParsedOutputKind.DownloadProgress:
                    lastDownloadedBytes = parsed.DownloadedBytes ?? lastDownloadedBytes;

                    var isNearLiveEdge = false;
                    double livePct = 0;
                    if (parsed.FragmentIndex is > 0 && parsed.FragmentCount is > 0)
                    {
                        livePct = Math.Clamp((double)parsed.FragmentIndex.Value / parsed.FragmentCount.Value * 100.0, 0, 100);
                        // Assume conservative 2s-5s per YouTube fragment; trigger Mode C stop when within margin
                        var remainingFragments = parsed.FragmentCount.Value - parsed.FragmentIndex.Value;
                        var estimatedRemainingSec = remainingFragments * 2.0;
                        isNearLiveEdge = estimatedRemainingSec <= req.LiveEdgeMarginSeconds;

                        if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge
                            && isNearLiveEdge
                            && parsed.FragmentCount.Value > 10
                            && !modeCWatchdogTriggered)
                        {
                            modeCWatchdogTriggered = true;
                            try { activeStopCts.Cancel(); } catch { }
                        }
                    }

                    var liveState = mode == LivestreamRecordingMode.RecordFromStartUntilEnd && isNearLiveEdge
                        ? DownloadJobState.RecordingLive
                        : (mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp);

                    var livePhase = liveState == DownloadJobState.RecordingLive
                        ? "Recording live..."
                        : (parsed.FragmentIndex is > 0 && parsed.FragmentCount is > 0
                            ? $"Catching up ({parsed.FragmentIndex}/{parsed.FragmentCount} segments)..."
                            : "Catching up from start...");

                    Report(runtime, new DownloadProgressSnapshot(
                        req.JobId, liveState, livePct,
                        liveState == DownloadJobState.RecordingLive,
                        lastDownloadedBytes, parsed.TotalBytes, parsed.SpeedBytesPerSecond, parsed.Eta,
                        lastRecordedDuration, livePhase, NonFatalNotice: latestNotice));
                    break;

                case ParsedOutputKind.PostProcess:
                    Report(runtime, new DownloadProgressSnapshot(
                        req.JobId, DownloadJobState.Finalizing, 96, true, lastDownloadedBytes, null, null, null,
                        lastRecordedDuration, "Finalizing recording...", NonFatalNotice: latestNotice));
                    break;
            }
        }
    }

    public static TimeSpan? CalculateModeCTargetDuration(DateTimeOffset? streamStartUtc, int liveEdgeMarginSeconds, DateTimeOffset? nowUtc = null)
    {
        if (!streamStartUtc.HasValue)
            return null;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var elapsed = now - streamStartUtc.Value;
        if (elapsed <= TimeSpan.Zero)
            return null;

        // Clamp to YouTube's practical ~120-hour DASH DVR cap
        var maxDvrWindow = TimeSpan.FromHours(120);
        var availableHistory = elapsed > maxDvrWindow ? maxDvrWindow : elapsed;
        var margin = TimeSpan.FromSeconds(Math.Clamp(liveEdgeMarginSeconds, 15, 300));
        var target = availableHistory - margin;

        return target >= TimeSpan.FromSeconds(15) ? target : null;
    }

    private static string? ResolveFinalOutputFile(string? printedFinal, string? lastDestination, OutputPathReservation reservation)
    {
        if (!string.IsNullOrWhiteSpace(printedFinal) && File.Exists(printedFinal))
            return printedFinal;

        if (!string.IsNullOrWhiteSpace(lastDestination) && File.Exists(lastDestination))
            return lastDestination;

        if (File.Exists(reservation.TargetFilePath))
            return reservation.TargetFilePath;

        // Check common finalized/crash-safe extensions matching our reserved base path
        var dir = Path.GetDirectoryName(reservation.ReservedBasePath);
        var baseName = Path.GetFileName(reservation.ReservedBasePath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            var candidates = Directory.EnumerateFiles(dir, $"{baseName}.*")
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];

            // For gracefully interrupted live MPEG-TS captures, check .part as last resort if valid
            var partFile = Directory.EnumerateFiles(dir, $"{baseName}.*.part")
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(partFile) && new FileInfo(partFile).Length > 0)
            {
                var finalizedPath = partFile[..^5]; // strip ".part"
                try
                {
                    File.Move(partFile, finalizedPath, overwrite: true);
                    return finalizedPath;
                }
                catch
                {
                    return partFile;
                }
            }
        }

        return null;
    }

    private static void CleanupPartialFiles(string reservedBasePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(reservedBasePath);
            var baseName = Path.GetFileName(reservedBasePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            foreach (var file in Directory.EnumerateFiles(dir, $"{baseName}.*"))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(".f", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // Best-effort cleanup of cancelled VOD temp files.
        }
    }

    private async Task PersistHistorySafeAsync(DownloadJobRequest req, string? outputFile, long fileSize)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
            return;

        try
        {
            await _historyStore.AddAsync(new DownloadHistoryEntry
            {
                VideoId = req.VideoId,
                Title = req.Title,
                Channel = req.Channel,
                SourceUrl = req.SourceUrl,
                OutputFilePath = outputFile,
                ThumbnailUrl = req.ThumbnailUrl,
                QualityLabel = req.SelectedQuality.Label,
                Container = Path.GetExtension(outputFile).TrimStart('.').ToUpperInvariant(),
                FileSizeBytes = fileSize,
                DurationSeconds = req.KnownDurationSeconds,
                WasLivestream = req.LiveStatus is MediaLiveStatus.IsLive or MediaLiveStatus.WasLive,
                LivestreamMode = req.LivestreamMode,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist download history entry.");
        }
    }

    private static void MarkCancelledAndCleanup(JobRuntimeState runtime, string? outputFile)
    {
        Report(runtime, new DownloadProgressSnapshot(
            runtime.Request.JobId,
            DownloadJobState.Cancelled,
            0, false, null, null, null, null, null,
            "Cancelled", OutputFilePath: outputFile));
    }

    private static void Report(JobRuntimeState runtime, DownloadProgressSnapshot snapshot)
    {
        try
        {
            runtime.Progress?.Report(snapshot);
        }
        catch
        {
            // UI progress subscriber errors must never crash the orchestrator loop.
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        await CancelAllAsync(TimeSpan.FromSeconds(10));
        await _shutdownCts.CancelAsync();

        try
        {
            await _dispatcherTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore shutdown timeout.
        }

        _shutdownCts.Dispose();
        _concurrencyGate.Dispose();
    }

    private sealed record QueuedJob(JobRuntimeState Runtime);

    private sealed class JobRuntimeState(
        DownloadJobRequest request,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationTokenSource cts)
    {
        public DownloadJobRequest Request { get; } = request;
        public IProgress<DownloadProgressSnapshot>? Progress { get; } = progress;
        public CancellationTokenSource Cts { get; } = cts;
        public bool UserCancelled { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
