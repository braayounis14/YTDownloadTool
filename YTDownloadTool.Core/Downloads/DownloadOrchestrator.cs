using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Livestream;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public interface IDownloadOrchestrator : IAsyncDisposable, IDisposable
{
    int ActiveJobCount { get; }
    int MaxConcurrentDownloads { get; }
    event Action<DownloadProgressSnapshot>? JobProgressChanged;

    void UpdateMaxConcurrency(int maxConcurrentDownloads);
    Task<IReadOnlyList<PersistedDownloadJob>> InitializeAndRecoverAsync(CancellationToken ct = default);
    Task<int> PruneOrphanedTempDirectoriesAsync(CancellationToken ct = default);
    void PruneOrphanedTempDirectories();

    Task<string> EnqueueAsync(DownloadJobRequest request, IProgress<DownloadProgressSnapshot>? progress = null, CancellationToken ct = default);

    Task<bool> MoveJobUpAsync(string jobId, CancellationToken ct = default);
    Task<bool> MoveJobDownAsync(string jobId, CancellationToken ct = default);
    Task<bool> MoveJobToTopAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<PersistedDownloadJob>> GetQueuedJobsAsync(CancellationToken ct = default);

    Task<bool> PauseJobAsync(string jobId, CancellationToken ct = default);
    Task<bool> ResumeJobAsync(string jobId, IProgress<DownloadProgressSnapshot>? progress = null, CancellationToken ct = default);

    Task<DownloadProgressSnapshot> RecoverPartialLivestreamAsync(string jobId, IProgress<DownloadProgressSnapshot>? progress = null, CancellationToken ct = default);
    Task<bool> DiscardJobAsync(string jobId, CancellationToken ct = default);

    bool RequestCancel(string jobId);
    Task CancelAllAsync(TimeSpan? timeout = null);
}

public sealed class DownloadOrchestrator : IDownloadOrchestrator
{
    private readonly IAppPaths _paths;
    private readonly IToolManager _toolManager;
    private readonly IProcessRunner _processRunner;
    private readonly ISettingsStore _settingsStore;
    private readonly IHistoryStore _historyStore;
    private readonly OutputPathGuard _outputPathGuard;
    private readonly IDownloadJobRepository _jobRepository;
    private readonly IDiagnosticsRepository _diagnosticsRepository;
    private readonly IDiskSpaceReservationLedger _diskLedger;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IMediaIntegrityService _mediaIntegrityService;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task>? _customRetryDelayAsync;

    private readonly ConcurrentDictionary<string, JobRuntimeState> _activeWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IProgress<DownloadProgressSnapshot>> _progressSubscribers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
    private readonly Task _dispatcherTask;

    private volatile int _maxConcurrentDownloads;
    private int _runningWorkerCount;
    private int _disposed;

    public DownloadOrchestrator(
        IAppPaths paths,
        IToolManager toolManager,
        IProcessRunner processRunner,
        ISettingsStore settingsStore,
        IHistoryStore historyStore,
        OutputPathGuard outputPathGuard,
        IDownloadJobRepository jobRepository,
        IDiagnosticsRepository diagnosticsRepository,
        IDiskSpaceReservationLedger diskLedger,
        IMediaProbeService mediaProbeService,
        IMediaIntegrityService mediaIntegrityService,
        ILogger<DownloadOrchestrator> logger,
        Func<TimeSpan, CancellationToken, Task>? customRetryDelayAsync = null)
    {
        _paths = paths;
        _toolManager = toolManager;
        _processRunner = processRunner;
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _outputPathGuard = outputPathGuard;
        _jobRepository = jobRepository;
        _diagnosticsRepository = diagnosticsRepository;
        _diskLedger = diskLedger;
        _mediaProbeService = mediaProbeService;
        _mediaIntegrityService = mediaIntegrityService;
        _logger = logger;
        _customRetryDelayAsync = customRetryDelayAsync;

        _maxConcurrentDownloads = Math.Clamp(_settingsStore.Current.MaxConcurrentDownloads, 1, 4);
        _dispatcherTask = Task.Run(DispatchLoopAsync);
    }

    public int ActiveJobCount => Volatile.Read(ref _runningWorkerCount);
    public int MaxConcurrentDownloads => _maxConcurrentDownloads;
    public event Action<DownloadProgressSnapshot>? JobProgressChanged;

    public void UpdateMaxConcurrency(int maxConcurrentDownloads)
    {
        var clamped = Math.Clamp(maxConcurrentDownloads, 1, 4);
        var previous = _maxConcurrentDownloads;
        _maxConcurrentDownloads = clamped;

        if (clamped > previous)
        {
            SignalScheduler();
        }
    }

    public async Task<IReadOnlyList<PersistedDownloadJob>> InitializeAndRecoverAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var nonTerminal = await _jobRepository.GetNonTerminalJobsAsync(ct).ConfigureAwait(false);
        var hasRunnableQueuedJobs = false;

        foreach (var job in nonTerminal)
        {
            if (job.Status == DownloadJobState.Queued)
            {
                var reserve = _diskLedger.TryReserve(
                    job.JobId,
                    job.TempDirectory,
                    job.OutputDirectory,
                    job.EstimatedBytes,
                    job.IsActiveLivestream);

                if (!reserve.Succeeded)
                {
                    await _jobRepository.UpdateJobStateAsync(
                        job.JobId,
                        DownloadJobState.Paused,
                        lastErrorCategory: DownloadErrorCategory.DiskOrPermission.ToString(),
                        lastErrorMessage: reserve.FailureMessage ?? "Paused on startup — insufficient free disk space.",
                        ct: ct).ConfigureAwait(false);
                }
                else
                {
                    hasRunnableQueuedJobs = true;
                }
            }
            else if (job.Status is DownloadJobState.Preparing
                or DownloadJobState.Downloading
                or DownloadJobState.CatchingUp
                or DownloadJobState.RecordingLive
                or DownloadJobState.Finalizing
                or DownloadJobState.Retrying)
            {
                if (job.IsActiveLivestream)
                {
                    if (HasRecoverableLivestreamMedia(job.TempDirectory))
                    {
                        await _jobRepository.UpdateJobStateAsync(
                            job.JobId,
                            DownloadJobState.InterruptedLivestream,
                            lastErrorMessage: "Livestream recording was interrupted — partial media is ready to recover.",
                            ct: ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _jobRepository.UpdateJobStateAsync(
                            job.JobId,
                            DownloadJobState.Failed,
                            lastErrorCategory: DownloadErrorCategory.Unknown.ToString(),
                            lastErrorMessage: "Livestream recording was interrupted before any media was captured.",
                            ct: ct).ConfigureAwait(false);
                        TryDeleteDirectoryWithRetry(job.TempDirectory);
                    }
                }
                else
                {
                    await _jobRepository.UpdateJobStateAsync(
                        job.JobId,
                        DownloadJobState.Paused,
                        lastErrorMessage: "Interrupted — ready to resume",
                        ct: ct).ConfigureAwait(false);
                }
            }
        }

        var refreshed = await _jobRepository.GetNonTerminalJobsAsync(ct).ConfigureAwait(false);
        if (hasRunnableQueuedJobs)
        {
            SignalScheduler();
        }

        return refreshed;
    }

    public async Task<int> PruneOrphanedTempDirectoriesAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_paths.TempDownloadsDirectory))
                return 0;

            var protectedSnap = await _jobRepository.GetProtectedTempDirectoryNamesAsync(ct).ConfigureAwait(false);
            if (!protectedSnap.CanSafelyPrune)
            {
                _logger.LogWarning("Skipping orphaned temp cleanup because SQLite database is in recovery mode.");
                return 0;
            }

            var deletedCount = 0;
            foreach (var dir in Directory.EnumerateDirectories(_paths.TempDownloadsDirectory))
            {
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderName))
                    continue;

                if (_activeWorkers.ContainsKey(folderName)
                    || protectedSnap.ProtectedFolderNames.Contains(folderName))
                {
                    continue;
                }

                TryDeleteDirectoryWithRetry(dir);
                if (!Directory.Exists(dir))
                    deletedCount++;
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error pruning orphaned temp download directories.");
            return 0;
        }
    }

    public void PruneOrphanedTempDirectories()
    {
        try
        {
            Task.Run(() => PruneOrphanedTempDirectoriesAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during synchronous orphan temp prune.");
        }
    }

    public async Task<string> EnqueueAsync(
        DownloadJobRequest request,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var jobId = string.IsNullOrWhiteSpace(request.JobId) ? Guid.NewGuid().ToString("N") : request.JobId;
        var normalizedReq = request with { JobId = jobId };
        var jobTempDir = BuildJobTempDirectory(jobId);

        if (_activeWorkers.ContainsKey(jobId))
            throw new InvalidOperationException($"A download job with ID '{jobId}' is already active.");

        var isLivestream = normalizedReq.LiveStatus == MediaLiveStatus.IsLive;
        var reservationResult = _diskLedger.TryReserve(
            jobId,
            jobTempDir,
            normalizedReq.OutputDirectory,
            normalizedReq.SelectedQuality.EstimatedSizeBytes,
            isLivestream);

        if (!reservationResult.Succeeded)
        {
            throw new IOException(reservationResult.FailureMessage ?? "Insufficient free disk space.");
        }

        if (progress is not null)
        {
            _progressSubscribers[jobId] = progress;
        }

        try
        {
            var persisted = PersistedDownloadJob.FromRequest(normalizedReq, jobTempDir);
            await _jobRepository.InsertQueuedJobAsync(persisted, ct).ConfigureAwait(false);
        }
        catch
        {
            _diskLedger.Release(jobId);
            _progressSubscribers.TryRemove(jobId, out _);
            throw;
        }

        EmitProgress(jobId, new DownloadProgressSnapshot(
            JobId: jobId,
            State: DownloadJobState.Queued,
            Percentage: 0,
            IsIndeterminate: false,
            DownloadedBytes: null,
            TotalBytes: normalizedReq.SelectedQuality.EstimatedSizeBytes,
            SpeedBytesPerSecond: null,
            Eta: null,
            RecordedDuration: null,
            PhaseLabel: "Queued..."));

        SignalScheduler();
        return jobId;
    }

    public Task<bool> MoveJobUpAsync(string jobId, CancellationToken ct = default)
        => _jobRepository.MoveQueuedJobUpAsync(jobId, ct);

    public Task<bool> MoveJobDownAsync(string jobId, CancellationToken ct = default)
        => _jobRepository.MoveQueuedJobDownAsync(jobId, ct);

    public Task<bool> MoveJobToTopAsync(string jobId, CancellationToken ct = default)
        => _jobRepository.MoveQueuedJobToTopAsync(jobId, ct);

    public Task<IReadOnlyList<PersistedDownloadJob>> GetQueuedJobsAsync(CancellationToken ct = default)
        => _jobRepository.GetQueuedJobsAsync(ct);

    public async Task<bool> PauseJobAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        if (_activeWorkers.TryGetValue(jobId, out var runtime))
        {
            // Pause is explicitly disabled on active livestreams (livestreams use Stop & Save)
            if (runtime.Request.LiveStatus == MediaLiveStatus.IsLive)
                return false;

            runtime.UserPaused = true;
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

        var job = await _jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || job.IsActiveLivestream || job.Status != DownloadJobState.Queued)
            return false;

        var updated = await _jobRepository.UpdateJobStateAsync(
            jobId,
            DownloadJobState.Paused,
            lastErrorMessage: "Paused — ready to resume",
            ct: ct).ConfigureAwait(false);

        if (updated)
        {
            _diskLedger.Release(jobId);
            EmitProgress(jobId, new DownloadProgressSnapshot(
                JobId: jobId,
                State: DownloadJobState.Paused,
                Percentage: job.ProgressPercent,
                IsIndeterminate: false,
                DownloadedBytes: job.DownloadedBytes,
                TotalBytes: job.TotalBytes,
                SpeedBytesPerSecond: null,
                Eta: null,
                RecordedDuration: null,
                PhaseLabel: "Paused — ready to resume"));
        }

        return updated;
    }

    public async Task<bool> ResumeJobAsync(
        string jobId,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        var job = await _jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || job.IsActiveLivestream || job.Status != DownloadJobState.Paused)
            return false;

        var reserve = _diskLedger.TryReserve(
            jobId,
            job.TempDirectory,
            job.OutputDirectory,
            job.EstimatedBytes,
            isLivestream: false);

        if (!reserve.Succeeded)
        {
            throw new IOException(reserve.FailureMessage ?? "Insufficient free disk space to resume download.");
        }

        if (progress is not null)
        {
            _progressSubscribers[jobId] = progress;
        }

        var updated = await _jobRepository.UpdateJobStateAsync(
            jobId,
            DownloadJobState.Queued,
            clearError: true,
            ct: ct).ConfigureAwait(false);

        if (!updated)
        {
            _diskLedger.Release(jobId);
            return false;
        }

        EmitProgress(jobId, new DownloadProgressSnapshot(
            JobId: jobId,
            State: DownloadJobState.Queued,
            Percentage: job.ProgressPercent,
            IsIndeterminate: false,
            DownloadedBytes: job.DownloadedBytes,
            TotalBytes: job.TotalBytes,
            SpeedBytesPerSecond: null,
            Eta: null,
            RecordedDuration: null,
            PhaseLabel: "Queued (resuming)..."));

        SignalScheduler();
        return true;
    }

    public async Task<DownloadProgressSnapshot> RecoverPartialLivestreamAsync(
        string jobId,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var job = await _jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job '{jobId}' was not found.");

        if (progress is not null)
        {
            _progressSubscribers[jobId] = progress;
        }

        var req = job.ToDownloadJobRequest();
        var jobTempDir = job.TempDirectory;

        EmitProgress(jobId, new DownloadProgressSnapshot(
            jobId, DownloadJobState.Finalizing, 40, true, job.DownloadedBytes, null, null, null, null,
            "Recovering partial livestream recording..."));

        try
        {
            if (!Directory.Exists(jobTempDir) || !HasRecoverableLivestreamMedia(jobTempDir))
            {
                var failedSnap = new DownloadProgressSnapshot(
                    jobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                    "Recovery failed",
                    ErrorMessage: "No recoverable partial recording fragments were found in the temp staging folder.");
                await _jobRepository.UpdateJobStateAsync(
                    jobId, DownloadJobState.Failed,
                    lastErrorCategory: DownloadErrorCategory.Unknown.ToString(),
                    lastErrorMessage: failedSnap.ErrorMessage,
                    ct: ct).ConfigureAwait(false);
                EmitProgress(jobId, failedSnap);
                return failedSnap;
            }

            var toolchain = await _toolManager.EnsureToolsReadyAsync(ct: ct).ConfigureAwait(false);
            var targetExt = string.IsNullOrWhiteSpace(req.OutputContainer) ? "mkv" : req.OutputContainer.ToLowerInvariant();
            var suffixTag = $"RECOVERED {DateTime.Now:yyyy-MM-dd HH-mm}";

            using var reservation = !string.IsNullOrWhiteSpace(job.RelativeBaseFileName)
                ? _outputPathGuard.ReserveExactOutputPath(req.OutputDirectory, job.RelativeBaseFileName, targetExt)
                : _outputPathGuard.ReserveUniqueOutputPath(req.OutputDirectory, req.Title, req.VideoId, targetExt, suffixTag);

            string? recoveredWorkingFile = null;
            string? latestNotice = null;

            // 1. Check for already-muxed complete video container (.mkv/.mp4/.ts)
            recoveredWorkingFile = FindExistingCompleteMediaInTemp(jobTempDir);

            // 2. Check for split video + audio .f* tracks (2-input ffmpeg -c copy -shortest mux)
            if (string.IsNullOrWhiteSpace(recoveredWorkingFile))
            {
                recoveredWorkingFile = await TryManualMuxPartialTracksAsync(toolchain, reservation.RelativeBaseFileName, jobTempDir, searchOutputDir: null).ConfigureAwait(false);
            }

            // 3. Check for single muxed .mkv.part / .ts.part / .mp4.part file and remux cleanly
            if (string.IsNullOrWhiteSpace(recoveredWorkingFile))
            {
                var singlePart = FindLargestSinglePartialMediaFile(jobTempDir);
                if (!string.IsNullOrWhiteSpace(singlePart))
                {
                    var remuxExt = targetExt.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mkv";
                    var remuxTarget = Path.Combine(jobTempDir, $"{reservation.RelativeBaseFileName}_recovered{remuxExt}");
                    var remuxArgs = MediaIntegrityService.BuildLosslessRepairArguments(singlePart, remuxTarget);
                    var remuxRes = await _processRunner.RunAsync(new ProcessRunRequest
                    {
                        ExecutablePath = toolchain.FFmpegPath,
                        Arguments = remuxArgs,
                        GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                        GracefulStopTimeout = TimeSpan.FromSeconds(5)
                    }, ct).ConfigureAwait(false);

                    if (remuxRes.ExitCode == 0 && File.Exists(remuxTarget) && new FileInfo(remuxTarget).Length > 0)
                    {
                        recoveredWorkingFile = remuxTarget;
                    }
                    else
                    {
                        // Fall back to MKV lossless copy if MP4 container rejected stream codecs
                        var fallbackMkv = Path.Combine(jobTempDir, $"{reservation.RelativeBaseFileName}_recovered.mkv");
                        var fallbackRes = await _processRunner.RunAsync(new ProcessRunRequest
                        {
                            ExecutablePath = toolchain.FFmpegPath,
                            Arguments = MediaIntegrityService.BuildLosslessRepairArguments(singlePart, fallbackMkv),
                            GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                            GracefulStopTimeout = TimeSpan.FromSeconds(5)
                        }, ct).ConfigureAwait(false);

                        if (fallbackRes.ExitCode == 0 && File.Exists(fallbackMkv) && new FileInfo(fallbackMkv).Length > 0)
                        {
                            recoveredWorkingFile = fallbackMkv;
                            latestNotice = "Recovered safely as MKV to preserve original stream codecs.";
                        }
                        else
                        {
                            recoveredWorkingFile = singlePart;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(recoveredWorkingFile) || !File.Exists(recoveredWorkingFile) || new FileInfo(recoveredWorkingFile).Length == 0)
            {
                var failedSnap = new DownloadProgressSnapshot(
                    jobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                    "Recovery failed",
                    ErrorMessage: "Partial stream fragments could not be muxed into a playable file.");
                await _jobRepository.UpdateJobStateAsync(
                    jobId, DownloadJobState.Failed,
                    lastErrorCategory: DownloadErrorCategory.Unknown.ToString(),
                    lastErrorMessage: failedSnap.ErrorMessage,
                    ct: ct).ConfigureAwait(false);
                EmitProgress(jobId, failedSnap);
                return failedSnap;
            }

            // Convert MKV/TS -> MP4 if user selected MP4
            if (targetExt.Equals("mp4", StringComparison.OrdinalIgnoreCase)
                && !recoveredWorkingFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var mp4Target = Path.Combine(jobTempDir, $"{reservation.RelativeBaseFileName}_recovered.mp4");
                var mp4Res = await _processRunner.RunAsync(new ProcessRunRequest
                {
                    ExecutablePath = toolchain.FFmpegPath,
                    Arguments = YtDlpArgumentBuilder.BuildRemuxToMp4Arguments(recoveredWorkingFile, mp4Target),
                    GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                }, ct).ConfigureAwait(false);

                if (mp4Res.ExitCode == 0 && File.Exists(mp4Target) && new FileInfo(mp4Target).Length > 0)
                {
                    recoveredWorkingFile = mp4Target;
                }
                else
                {
                    latestNotice = "Recovered safely in MKV/TS format (MP4 remux skipped to preserve stream codecs).";
                }
            }

            var finalExt = Path.GetExtension(recoveredWorkingFile);
            if (string.IsNullOrWhiteSpace(finalExt) || finalExt.Equals(".part", StringComparison.OrdinalIgnoreCase))
                finalExt = ".mkv";

            var preferredFileName = $"{reservation.RelativeBaseFileName}{finalExt}";
            var finalOutputPath = await OutputPathGuard.MoveFileCrossVolumeSafeAsync(
                recoveredWorkingFile, req.OutputDirectory, preferredFileName, ct).ConfigureAwait(false);

            EmitProgress(jobId, new DownloadProgressSnapshot(
                jobId, DownloadJobState.Finalizing, 95, true, null, null, null, null, null,
                "Verifying recovered recording with ffprobe..."));

            var integrity = await VerifyOutputSafeAsync(toolchain, finalOutputPath, req, jobTempDir).ConfigureAwait(false);
            var finalSize = File.Exists(finalOutputPath) ? new FileInfo(finalOutputPath).Length : integrity.FileSizeBytes;

            await PersistHistorySafeAsync(req, finalOutputPath, finalSize, integrity).ConfigureAwait(false);
            await _jobRepository.UpdateJobStateAsync(
                jobId,
                DownloadJobState.Completed,
                progressPercent: 100,
                downloadedBytes: finalSize,
                totalBytes: finalSize,
                outputFilePath: finalOutputPath,
                clearError: true,
                ct: CancellationToken.None).ConfigureAwait(false);

            _diskLedger.Release(jobId);
            TryDeleteDirectoryWithRetry(jobTempDir);

            var doneSnap = new DownloadProgressSnapshot(
                jobId, DownloadJobState.Completed, 100, false, finalSize, finalSize, null, TimeSpan.Zero,
                integrity.DurationSeconds is > 0 ? TimeSpan.FromSeconds(integrity.DurationSeconds.Value) : null,
                "Partial recording recovered & saved",
                OutputFilePath: finalOutputPath,
                NonFatalNotice: latestNotice,
                IntegrityStatus: integrity.Status);

            EmitProgress(jobId, doneSnap);
            return doneSnap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed recovering interrupted livestream job {JobId}.", jobId);
            var errSnap = new DownloadProgressSnapshot(
                jobId, DownloadJobState.InterruptedLivestream, 0, false, job.DownloadedBytes, null, null, null, null,
                "Recovery failed — partial files preserved",
                ErrorMessage: $"Recovery failed: {ex.Message}",
                TechnicalDetails: ex.ToString());
            EmitProgress(jobId, errSnap);
            return errSnap;
        }
    }

    public async Task<bool> DiscardJobAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        if (_activeWorkers.TryGetValue(jobId, out var runtime))
        {
            runtime.UserCancelled = true;
            try { runtime.Cts.Cancel(); } catch { }
            try { await runtime.Completion.Task.WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false); } catch { }
        }

        var job = await _jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        var tempDir = job?.TempDirectory ?? BuildJobTempDirectory(jobId);

        _diskLedger.Release(jobId);
        _progressSubscribers.TryRemove(jobId, out _);
        await _jobRepository.DeleteJobAsync(jobId, ct).ConfigureAwait(false);
        TryDeleteDirectoryWithRetry(tempDir);

        EmitProgress(jobId, new DownloadProgressSnapshot(
            jobId, DownloadJobState.Cancelled, 0, false, null, null, null, null, null,
            "Discarded"));

        return true;
    }

    public bool RequestCancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        if (_activeWorkers.TryGetValue(jobId, out var runtime))
        {
            runtime.UserCancelled = true;
            try
            {
                runtime.Cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        // Also allow cancelling a job waiting in the SQLite queue
        try
        {
            var job = _jobRepository.GetByIdAsync(jobId).GetAwaiter().GetResult();
            if (job is not null && job.Status == DownloadJobState.Queued)
            {
                _jobRepository.UpdateJobStateAsync(jobId, DownloadJobState.Cancelled, clearError: true).GetAwaiter().GetResult();
                _diskLedger.Release(jobId);
                TryDeleteDirectoryWithRetry(job.TempDirectory);
                EmitProgress(jobId, new DownloadProgressSnapshot(
                    jobId, DownloadJobState.Cancelled, 0, false, null, null, null, null, null, "Cancelled"));
                _progressSubscribers.TryRemove(jobId, out _);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error cancelling queued job {JobId}.", jobId);
        }

        return false;
    }

    public async Task CancelAllAsync(TimeSpan? timeout = null)
    {
        foreach (var (_, runtime) in _activeWorkers)
        {
            runtime.UserCancelled = true;
            try { runtime.Cts.Cancel(); } catch { }
        }

        var waitTasks = _activeWorkers.Values
            .Select(r => r.Completion.Task)
            .ToArray();

        if (waitTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(waitTasks).WaitAsync(timeout ?? TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for all active downloads to finalize on CancelAllAsync.");
            }
        }
    }

    private void SignalScheduler()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _wakeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await _wakeSignal.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);

                while (!_shutdownCts.IsCancellationRequested
                       && Volatile.Read(ref _runningWorkerCount) < _maxConcurrentDownloads)
                {
                    PersistedDownloadJob? nextJob;
                    try
                    {
                        nextJob = await _jobRepository.DequeueNextQueuedJobAsync(_shutdownCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error dequeuing next download job from SQLite.");
                        break;
                    }

                    if (nextJob is null)
                        break;

                    var req = nextJob.ToDownloadJobRequest();
                    var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                    var runtime = new JobRuntimeState(nextJob, req, linkedCts);

                    if (!_activeWorkers.TryAdd(nextJob.JobId, runtime))
                    {
                        linkedCts.Dispose();
                        continue;
                    }

                    Interlocked.Increment(ref _runningWorkerCount);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteJobAsync(runtime).ConfigureAwait(false);
                        }
                        finally
                        {
                            runtime.Completion.TrySetResult();
                            _activeWorkers.TryRemove(nextJob.JobId, out _);
                            runtime.Cts.Dispose();
                            Interlocked.Decrement(ref _runningWorkerCount);
                            SignalScheduler();
                        }
                    });
                }
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
        var jobTempDir = string.IsNullOrWhiteSpace(runtime.Persisted.TempDirectory)
            ? BuildJobTempDirectory(req.JobId)
            : runtime.Persisted.TempDirectory;
        var preserveTempDirectory = false;

        try
        {
            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Preparing, runtime.Persisted.ProgressPercent, true,
                runtime.Persisted.DownloadedBytes, req.SelectedQuality.EstimatedSizeBytes,
                null, null, null, "Preparing engine & staging area..."));

            _paths.EnsureDirectoriesExist();
            Directory.CreateDirectory(jobTempDir);

            // Re-ensure reservation exists (idempotent if already reserved at Enqueue/Resume/Startup)
            var isActiveLivestream = req.LiveStatus == MediaLiveStatus.IsLive;
            var diskReserve = _diskLedger.TryReserve(
                req.JobId, jobTempDir, req.OutputDirectory, req.SelectedQuality.EstimatedSizeBytes, isActiveLivestream);
            if (!diskReserve.Succeeded)
            {
                throw new IOException(diskReserve.FailureMessage ?? "Insufficient free disk space.");
            }

            var toolchain = await _toolManager.EnsureToolsReadyAsync(ct: ct).ConfigureAwait(false);

            var targetExt = req.SelectedQuality.IsAudioOnly
                ? (req.SelectedQuality.PreferredAudioExtension ?? req.OutputContainer)
                : req.OutputContainer;

            var suffixTag = isActiveLivestream ? $"LIVE {DateTime.Now:yyyy-MM-dd HH-mm}" : null;

            var hasPersistedBase = !string.IsNullOrWhiteSpace(runtime.Persisted.RelativeBaseFileName);
            using var reservation = hasPersistedBase
                ? _outputPathGuard.ReserveExactOutputPath(req.OutputDirectory, runtime.Persisted.RelativeBaseFileName!, targetExt)
                : _outputPathGuard.ReserveUniqueOutputPath(req.OutputDirectory, req.Title, req.VideoId, targetExt, suffixTag);

            // Persist RelativeBaseFileName immediately so pause or crash recovery reuses the exact .part filename template
            if (!hasPersistedBase)
            {
                await _jobRepository.UpdateJobStateAsync(
                    req.JobId,
                    DownloadJobState.Preparing,
                    relativeBaseFileName: reservation.RelativeBaseFileName,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }

            var isResumingExisting = hasPersistedBase && HasPartialFilesInDirectory(jobTempDir);

            if (isActiveLivestream)
            {
                preserveTempDirectory = await ExecuteLivestreamJobAsync(runtime, toolchain, reservation, jobTempDir, ct).ConfigureAwait(false);
            }
            else
            {
                preserveTempDirectory = await ExecuteStandardVideoJobAsync(runtime, toolchain, reservation, jobTempDir, isResumingExisting, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (runtime.UserPaused && req.LiveStatus != MediaLiveStatus.IsLive)
            {
                preserveTempDirectory = true;
                await HandleJobPausedAsync(runtime, runtime.Persisted.ProgressPercent, runtime.Persisted.DownloadedBytes, req.SelectedQuality.EstimatedSizeBytes).ConfigureAwait(false);
            }
            else
            {
                await HandleJobCancelledAsync(runtime, jobTempDir).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download job {JobId} failed.", req.JobId);
            var category = ex is IOException
                ? DownloadErrorCategory.DiskOrPermission
                : DownloadErrorCategory.Unknown;

            await RecordFailureAndPersistAsync(
                runtime,
                category.ToString(),
                ex.Message,
                ex.ToString(),
                exitCode: null,
                retryAttempt: 0).ConfigureAwait(false);

            Report(runtime, new DownloadProgressSnapshot(
                req.JobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                "Failed", ErrorMessage: ex.Message, TechnicalDetails: ex.ToString()));
        }
        finally
        {
            _diskLedger.Release(req.JobId);
            if (!preserveTempDirectory)
            {
                TryDeleteDirectoryWithRetry(jobTempDir);
                _progressSubscribers.TryRemove(req.JobId, out _);
            }
        }
    }

    private async Task<bool> ExecuteStandardVideoJobAsync(
        JobRuntimeState runtime,
        ResolvedToolchain toolchain,
        OutputPathReservation reservation,
        string jobTempDir,
        bool isResumingExisting,
        CancellationToken ct)
    {
        var activeReq = runtime.Request;
        var transientAttempt = 0;
        var reprobeAttempt = 0;
        double lastOverallPct = runtime.Persisted.ProgressPercent;
        long? lastDownloadedBytes = runtime.Persisted.DownloadedBytes;
        long? lastTotalBytes = activeReq.SelectedQuality.EstimatedSizeBytes;

        string? detectedFinalFile = null;
        string? lastDestination = null;
        string? latestNotice = null;

        await _jobRepository.UpdateJobStateAsync(
            activeReq.JobId,
            DownloadJobState.Downloading,
            relativeBaseFileName: reservation.RelativeBaseFileName,
            clearError: true,
            ct: CancellationToken.None).ConfigureAwait(false);

        while (true)
        {
            var speedLimitKbps = Math.Max(0, _settingsStore.Current.PerDownloadSpeedLimitKbps);
            var useContinue = isResumingExisting || transientAttempt > 0 || reprobeAttempt > 0;
            var args = YtDlpArgumentBuilder.BuildStandardDownloadArguments(
                toolchain,
                activeReq,
                reservation.OutputDirectory,
                jobTempDir,
                reservation.RelativeBaseFileName,
                perDownloadSpeedLimitKbps: speedLimitKbps,
                resumeExisting: useContinue);

            var streamIndex = 0;
            var lastStreamFinished = false;
            var expectsTwoStreams = !activeReq.SelectedQuality.IsAudioOnly && activeReq.SelectedQuality.RequiresFFmpeg;
            detectedFinalFile = null;
            lastDestination = null;

            var result = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.YtDlpPath,
                Arguments = args,
                GracefulStop = GracefulStopStrategy.CtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(4),
                OnStandardOutput = line => HandleStandardOutput(line),
                OnStandardError = line => HandleStandardOutput(line)
            }, ct).ConfigureAwait(false);

            if (runtime.UserPaused)
            {
                await HandleJobPausedAsync(runtime, lastOverallPct, lastDownloadedBytes, lastTotalBytes).ConfigureAwait(false);
                return true; // CRITICAL: Preserve jobTempDir & .part fragments for --continue resume!
            }

            if (result.WasCancelled || runtime.UserCancelled || ct.IsCancellationRequested)
            {
                await HandleJobCancelledAsync(runtime, jobTempDir).ConfigureAwait(false);
                return false;
            }

            if (result.ExitCode == 0)
            {
                break;
            }

            // Classify failure and apply smart retry policy
            var classified = YtDlpErrorClassifier.Classify(result.StandardError, result.ExitCode);
            var directive = classified.EffectiveRetry;

            if (directive.Strategy == RetryStrategy.ExponentialBackoff && transientAttempt < directive.MaxAttempts)
            {
                transientAttempt++;
                var delay = directive.ComputeDelay(transientAttempt);
                var shortReason = classified.Category == DownloadErrorCategory.RateLimited
                    ? "YouTube rate limit"
                    : "Connection interrupted";

                await _jobRepository.UpdateJobStateAsync(
                    activeReq.JobId,
                    DownloadJobState.Retrying,
                    progressPercent: lastOverallPct,
                    downloadedBytes: lastDownloadedBytes,
                    totalBytes: lastTotalBytes,
                    retryAttempt: transientAttempt,
                    maxRetryAttempts: directive.MaxAttempts,
                    lastErrorCategory: classified.Category.ToString(),
                    lastErrorMessage: classified.UserMessage,
                    ct: CancellationToken.None).ConfigureAwait(false);

                await WaitForRetryDelayAsync(
                    runtime,
                    delay,
                    lastOverallPct,
                    lastDownloadedBytes,
                    lastTotalBytes,
                    transientAttempt,
                    directive.MaxAttempts,
                    secsLeft => $"{shortReason} — retrying in {secsLeft}s (attempt {transientAttempt}/{directive.MaxAttempts})...",
                    ct).ConfigureAwait(false);

                continue;
            }

            if (directive.Strategy == RetryStrategy.ReprobeAndRetry && reprobeAttempt < directive.MaxAttempts)
            {
                reprobeAttempt++;
                var delay = directive.ComputeDelay(reprobeAttempt);

                await _jobRepository.UpdateJobStateAsync(
                    activeReq.JobId,
                    DownloadJobState.Retrying,
                    progressPercent: lastOverallPct,
                    downloadedBytes: lastDownloadedBytes,
                    totalBytes: lastTotalBytes,
                    retryAttempt: reprobeAttempt,
                    maxRetryAttempts: directive.MaxAttempts,
                    lastErrorCategory: classified.Category.ToString(),
                    lastErrorMessage: classified.UserMessage,
                    ct: CancellationToken.None).ConfigureAwait(false);

                await WaitForRetryDelayAsync(
                    runtime,
                    delay,
                    lastOverallPct,
                    lastDownloadedBytes,
                    lastTotalBytes,
                    reprobeAttempt,
                    directive.MaxAttempts,
                    secsLeft => $"Stream link expired (HTTP 403) — refreshing metadata in {secsLeft}s (attempt {reprobeAttempt}/{directive.MaxAttempts})...",
                    ct).ConfigureAwait(false);

                Report(runtime, new DownloadProgressSnapshot(
                    activeReq.JobId, DownloadJobState.Retrying, lastOverallPct, true,
                    lastDownloadedBytes, lastTotalBytes, null, null, null,
                    $"Refreshing expired stream link (attempt {reprobeAttempt}/{directive.MaxAttempts})...",
                    RetryAttempt: reprobeAttempt, MaxRetryAttempts: directive.MaxAttempts));

                var refreshed = await TryReprobeAndRefreshRequestAsync(activeReq, ct).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    activeReq = refreshed;
                    latestNotice = "Refreshed expired stream link automatically.";
                }

                continue;
            }

            // Permanent failure or retries exhausted
            var totalAttemptsUsed = Math.Max(transientAttempt, reprobeAttempt);
            await RecordFailureAndPersistAsync(
                runtime,
                classified.Category.ToString(),
                classified.UserMessage,
                classified.TechnicalSummary,
                result.ExitCode,
                totalAttemptsUsed).ConfigureAwait(false);

            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Failed, lastOverallPct, false,
                lastDownloadedBytes, lastTotalBytes, null, null, null,
                "Failed", ErrorMessage: classified.UserMessage, TechnicalDetails: classified.TechnicalSummary,
                RetryAttempt: totalAttemptsUsed, MaxRetryAttempts: directive.MaxAttempts));
            return false;

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
                            activeReq.JobId, DownloadJobState.Finalizing, 96, true, lastDownloadedBytes, lastTotalBytes,
                            null, null, null, "Merging & finalizing with FFmpeg...", NonFatalNotice: latestNotice));
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

                        lastOverallPct = overallPct;
                        lastDownloadedBytes = parsed.DownloadedBytes ?? lastDownloadedBytes;
                        lastTotalBytes = parsed.TotalBytes ?? lastTotalBytes;

                        var phase = activeReq.SelectedQuality.IsAudioOnly
                            ? "Downloading audio..."
                            : (parsed.HasVideo && !parsed.HasAudio
                                ? "Downloading video stream..."
                                : (!parsed.HasVideo && parsed.HasAudio ? "Downloading audio stream..." : "Downloading..."));

                        Report(runtime, new DownloadProgressSnapshot(
                            activeReq.JobId, DownloadJobState.Downloading, overallPct,
                            parsed.TotalBytes is null && parsed.FragmentCount is null,
                            parsed.DownloadedBytes, parsed.TotalBytes, parsed.SpeedBytesPerSecond, parsed.Eta,
                            null, phase, NonFatalNotice: latestNotice));
                        break;
                }
            }
        }

        var locatedFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation, jobTempDir, allowPartFallback: false);
        if (string.IsNullOrWhiteSpace(locatedFile) || !File.Exists(locatedFile))
        {
            const string missingMsg = "Download completed, but the output file could not be located.";
            await RecordFailureAndPersistAsync(runtime, DownloadErrorCategory.Unknown.ToString(), missingMsg, missingMsg, 0, transientAttempt + reprobeAttempt).ConfigureAwait(false);
            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Failed, 0, false, null, null, null, null, null,
                "Failed", ErrorMessage: missingMsg));
            return false;
        }

        // Ensure final file resides in the user's Save folder (cross-volume safe if yt-dlp left it in temp)
        string finalFile;
        try
        {
            finalFile = await OutputPathGuard.MoveFileCrossVolumeSafeAsync(
                locatedFile, reservation.OutputDirectory, Path.GetFileName(locatedFile), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move completed file '{Src}' into '{Dest}'.", locatedFile, reservation.OutputDirectory);
            var errMsg = $"Could not move finished download to '{reservation.OutputDirectory}': {ex.Message}";
            await RecordFailureAndPersistAsync(runtime, DownloadErrorCategory.DiskOrPermission.ToString(), errMsg, $"Temporary file preserved at: {locatedFile}", null, 0).ConfigureAwait(false);
            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Failed, 99, false, null, null, null, null, null,
                "Failed to save output file",
                ErrorMessage: errMsg,
                TechnicalDetails: $"Temporary file preserved at: {locatedFile}"));
            return true; // Preserve jobTempDir so user doesn't lose the downloaded file
        }

        Report(runtime, new DownloadProgressSnapshot(
            activeReq.JobId, DownloadJobState.Finalizing, 98, true, lastDownloadedBytes, lastTotalBytes,
            null, null, null, "Verifying file integrity...", NonFatalNotice: latestNotice));

        var integrity = await VerifyOutputSafeAsync(toolchain, finalFile, activeReq, jobTempDir).ConfigureAwait(false);
        if (integrity.Status == MediaIntegrityStatus.Repaired)
        {
            latestNotice = string.IsNullOrWhiteSpace(latestNotice)
                ? "Container metadata repaired automatically via FFmpeg."
                : $"{latestNotice} • Container metadata repaired automatically.";
        }

        var finalSize = File.Exists(finalFile) ? new FileInfo(finalFile).Length : integrity.FileSizeBytes;
        await PersistHistorySafeAsync(activeReq, finalFile, finalSize, integrity).ConfigureAwait(false);
        await _jobRepository.UpdateJobStateAsync(
            activeReq.JobId,
            DownloadJobState.Completed,
            progressPercent: 100,
            downloadedBytes: finalSize,
            totalBytes: finalSize,
            outputFilePath: finalFile,
            clearError: true,
            ct: CancellationToken.None).ConfigureAwait(false);

        TryDeleteDirectoryWithRetry(jobTempDir);

        var finalDuration = integrity.DurationSeconds is > 0
            ? TimeSpan.FromSeconds(integrity.DurationSeconds.Value)
            : (activeReq.KnownDurationSeconds is > 0 ? TimeSpan.FromSeconds(activeReq.KnownDurationSeconds.Value) : (TimeSpan?)null);

        Report(runtime, new DownloadProgressSnapshot(
            activeReq.JobId, DownloadJobState.Completed, 100, false, finalSize > 0 ? finalSize : null,
            finalSize > 0 ? finalSize : null, null, TimeSpan.Zero,
            finalDuration,
            "Completed", OutputFilePath: finalFile, NonFatalNotice: latestNotice, IntegrityStatus: integrity.Status));

        return false;
    }

    private async Task<bool> ExecuteLivestreamJobAsync(
        JobRuntimeState runtime,
        ResolvedToolchain toolchain,
        OutputPathReservation reservation,
        string jobTempDir,
        CancellationToken ct)
    {
        var activeReq = runtime.Request;
        var mode = activeReq.LivestreamMode ?? LivestreamRecordingMode.RecordFromNow;

        if (mode != LivestreamRecordingMode.RecordFromNow && !activeReq.IsDvrAvailable)
        {
            throw new InvalidOperationException(
                "This livestream does not expose DVR/rewind history. Please choose 'Record from now' (Mode A).");
        }

        TimeSpan? modeCTargetDuration = null;
        if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge)
        {
            modeCTargetDuration = CalculateModeCTargetDuration(activeReq.LivestreamStartUtc, activeReq.LiveEdgeMarginSeconds);
            if (activeReq.LivestreamStartUtc.HasValue && modeCTargetDuration is null)
            {
                throw new InvalidOperationException(
                    $"This livestream has only been live for less than the configured {activeReq.LiveEdgeMarginSeconds}s live-edge margin. Wait a moment or use Mode A ('Record from now').");
            }
        }

        await _jobRepository.UpdateJobStateAsync(
            activeReq.JobId,
            mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp,
            relativeBaseFileName: reservation.RelativeBaseFileName,
            clearError: true,
            ct: CancellationToken.None).ConfigureAwait(false);

        var transientAttempt = 0;
        var reprobeAttempt = 0;

        string? detectedFinalFile = null;
        string? lastDestination = null;
        string? latestNotice = null;
        TimeSpan? lastRecordedDuration = null;
        long? lastDownloadedBytes = null;
        var modeCStopTriggered = false;
        ProcessRunResult lastRunResult;
        string? capturedFile;

        while (true)
        {
            var speedLimitKbps = Math.Max(0, _settingsStore.Current.PerDownloadSpeedLimitKbps);
            var args = YtDlpArgumentBuilder.BuildLivestreamArguments(
                toolchain,
                activeReq,
                reservation.OutputDirectory,
                jobTempDir,
                reservation.RelativeBaseFileName,
                mode,
                perDownloadSpeedLimitKbps: speedLimitKbps);

            var isCappedEstimate = activeReq.LivestreamStartUtc.HasValue
                                   && (DateTimeOffset.UtcNow - activeReq.LivestreamStartUtc.Value).TotalHours > 120;
            var expectedTracks = activeReq.SelectedQuality.RequiresFFmpeg ? 2 : 1;
            var watchdog = mode == LivestreamRecordingMode.RecordFromStartToLiveEdge
                ? new ModeCLiveEdgeWatchdog(activeReq.LivestreamStartUtc, activeReq.LiveEdgeMarginSeconds, isCappedEstimate, expectedTracks)
                : null;

            using var jobLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            modeCStopTriggered = false;
            detectedFinalFile = null;
            lastDestination = null;
            var bytesByTrack = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            var initialPhase = mode switch
            {
                LivestreamRecordingMode.RecordFromNow => "Recording live stream...",
                LivestreamRecordingMode.RecordFromStartUntilEnd => "Catching up from stream start...",
                LivestreamRecordingMode.RecordFromStartToLiveEdge => modeCTargetDuration.HasValue
                    ? $"Capturing start → ~{FormatDuration(modeCTargetDuration.Value)} (stopping {activeReq.LiveEdgeMarginSeconds}s before live)..."
                    : $"Capturing historical stream (stopping {activeReq.LiveEdgeMarginSeconds}s before live)...",
                _ => "Recording live stream..."
            };

            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId,
                mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp,
                0, mode != LivestreamRecordingMode.RecordFromStartToLiveEdge,
                null, null, null, null, TimeSpan.Zero, initialPhase));

            lastRunResult = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.YtDlpPath,
                Arguments = args,
                GracefulStop = GracefulStopStrategy.CtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(15),
                OnStandardOutput = line => HandleLiveLine(line),
                OnStandardError = line => HandleLiveLine(line)
            }, jobLinkedCts.Token).ConfigureAwait(false);

            // Locate finalized merged file in Save folder or jobTempDir; if separate unmerged .f* video+audio
            // tracks remain inside jobTempDir after stop/disconnect, mux them cleanly with FFmpeg (-c copy -shortest) FIRST.
            capturedFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation, jobTempDir, allowPartFallback: false);
            if (string.IsNullOrWhiteSpace(capturedFile))
            {
                capturedFile = await TryManualMuxPartialTracksAsync(toolchain, reservation.RelativeBaseFileName, jobTempDir, reservation.OutputDirectory).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(capturedFile))
            {
                capturedFile = ResolveFinalOutputFile(detectedFinalFile, lastDestination, reservation, jobTempDir, allowPartFallback: true);
            }

            var hasSavedRecording = !string.IsNullOrWhiteSpace(capturedFile)
                                    && File.Exists(capturedFile)
                                    && new FileInfo(capturedFile).Length > 0;

            if (hasSavedRecording || runtime.UserCancelled || modeCStopTriggered || ct.IsCancellationRequested)
                break;

            // No recording captured yet and yt-dlp exited with failure -> evaluate smart retry
            var classified = YtDlpErrorClassifier.Classify(lastRunResult.StandardError, lastRunResult.ExitCode);
            var directive = classified.EffectiveRetry;

            if (directive.Strategy == RetryStrategy.ExponentialBackoff && transientAttempt < directive.MaxAttempts)
            {
                transientAttempt++;
                var delay = directive.ComputeDelay(transientAttempt);
                await WaitForRetryDelayAsync(
                    runtime, delay, 0, lastDownloadedBytes, null,
                    transientAttempt, directive.MaxAttempts,
                    secsLeft => $"Livestream connection failed — retrying in {secsLeft}s (attempt {transientAttempt}/{directive.MaxAttempts})...",
                    ct).ConfigureAwait(false);
                continue;
            }

            if (directive.Strategy == RetryStrategy.ReprobeAndRetry && reprobeAttempt < directive.MaxAttempts)
            {
                reprobeAttempt++;
                var delay = directive.ComputeDelay(reprobeAttempt);
                await WaitForRetryDelayAsync(
                    runtime, delay, 0, lastDownloadedBytes, null,
                    reprobeAttempt, directive.MaxAttempts,
                    secsLeft => $"Live manifest expired (HTTP 403) — refreshing in {secsLeft}s (attempt {reprobeAttempt}/{directive.MaxAttempts})...",
                    ct).ConfigureAwait(false);

                var refreshed = await TryReprobeAndRefreshRequestAsync(activeReq, ct).ConfigureAwait(false);
                if (refreshed is not null)
                    activeReq = refreshed;
                continue;
            }

            break;

            void HandleLiveLine(string line)
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

                    case ParsedOutputKind.LiveHeadWait:
                        if (watchdog is not null && !modeCStopTriggered)
                        {
                            var waitDecision = watchdog.ObserveLiveHeadWaitSignal();
                            if (waitDecision.ShouldStopNow)
                            {
                                modeCStopTriggered = true;
                                latestNotice = waitDecision.Reason;
                                Report(runtime, new DownloadProgressSnapshot(
                                    activeReq.JobId, DownloadJobState.Finalizing, 96, true, lastDownloadedBytes, null, null, null,
                                    lastRecordedDuration, "Reached live edge — finalizing recording...", NonFatalNotice: latestNotice));
                                try { jobLinkedCts.Cancel(); } catch { }
                            }
                        }
                        break;

                    case ParsedOutputKind.FFmpegProgress:
                        lastRecordedDuration = parsed.MediaTime ?? lastRecordedDuration;
                        lastDownloadedBytes = parsed.DownloadedBytes ?? lastDownloadedBytes;

                        var state = mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp;
                        var phase = lastRecordedDuration.HasValue
                            ? $"Recording live ({FormatDuration(lastRecordedDuration.Value)})"
                            : "Recording live...";

                        Report(runtime, new DownloadProgressSnapshot(
                            activeReq.JobId, state, 0, true,
                            lastDownloadedBytes, null, null, null, lastRecordedDuration, phase, NonFatalNotice: latestNotice));
                        break;

                    case ParsedOutputKind.DownloadProgress:
                        var trackKey = parsed.FormatId ?? parsed.StreamChannel ?? "0";
                        if (parsed.DownloadedBytes is > 0)
                        {
                            bytesByTrack[trackKey] = parsed.DownloadedBytes.Value;
                            lastDownloadedBytes = bytesByTrack.Values.Sum();
                        }

                        var livePct = 0.0;
                        var liveState = mode == LivestreamRecordingMode.RecordFromNow ? DownloadJobState.RecordingLive : DownloadJobState.CatchingUp;
                        string livePhase;

                        if (watchdog is not null && parsed.FragmentIndex is >= 0)
                        {
                            var decision = watchdog.ObserveFragmentProgress(trackKey, parsed.FragmentIndex.Value, parsed.FragmentCount);
                            livePct = decision.CatchUpProgressPercent;

                            if (decision.MinFragmentIndex is > 0)
                            {
                                lastRecordedDuration = TimeSpan.FromSeconds(decision.MinFragmentIndex.Value * watchdog.EstimatedFragmentDurationSeconds);
                            }

                            if (decision.ShouldStopNow && !modeCStopTriggered)
                            {
                                modeCStopTriggered = true;
                                latestNotice = decision.Reason;
                                Report(runtime, new DownloadProgressSnapshot(
                                    activeReq.JobId, DownloadJobState.Finalizing, 96, true, lastDownloadedBytes, null, null, null,
                                    lastRecordedDuration, "Reached live-edge target — merging & finalizing...", NonFatalNotice: latestNotice));
                                try { jobLinkedCts.Cancel(); } catch { }
                                break;
                            }

                            livePhase = decision.TargetStopFragment is > 0
                                ? $"Capturing start → live edge ({decision.MinFragmentIndex}/{decision.TargetStopFragment} segments)"
                                : $"Capturing historical stream (segment {decision.MinFragmentIndex})...";
                        }
                        else if (parsed.FragmentIndex is >= 0 && parsed.FragmentCount is > 0)
                        {
                            livePct = Math.Clamp((double)parsed.FragmentIndex.Value / parsed.FragmentCount.Value * 100.0, 0, 100);
                            var isNearEdge = (parsed.FragmentCount.Value - parsed.FragmentIndex.Value) <= 6;
                            if (mode == LivestreamRecordingMode.RecordFromStartUntilEnd && isNearEdge)
                                liveState = DownloadJobState.RecordingLive;

                            livePhase = liveState == DownloadJobState.RecordingLive
                                ? "Recording live..."
                                : $"Catching up ({parsed.FragmentIndex}/{parsed.FragmentCount} segments)...";
                        }
                        else
                        {
                            livePhase = liveState == DownloadJobState.RecordingLive
                                ? "Recording live..."
                                : "Catching up from start...";
                        }

                        Report(runtime, new DownloadProgressSnapshot(
                            activeReq.JobId, liveState, livePct,
                            liveState == DownloadJobState.RecordingLive || (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge && livePct <= 0),
                            lastDownloadedBytes, parsed.TotalBytes, parsed.SpeedBytesPerSecond, parsed.Eta,
                            lastRecordedDuration, livePhase, NonFatalNotice: latestNotice));
                        break;

                    case ParsedOutputKind.PostProcess:
                        Report(runtime, new DownloadProgressSnapshot(
                            activeReq.JobId, DownloadJobState.Finalizing, 97, true, lastDownloadedBytes, null, null, null,
                            lastRecordedDuration, "Merging & finalizing recording...", NonFatalNotice: latestNotice));
                        break;
                }
            }
        }

        var hasValidOutput = !string.IsNullOrWhiteSpace(capturedFile)
                             && File.Exists(capturedFile)
                             && new FileInfo(capturedFile).Length > 0;

        if (!hasValidOutput)
        {
            if (runtime.UserCancelled)
            {
                await HandleJobCancelledAsync(runtime, jobTempDir).ConfigureAwait(false);
                return false;
            }

            var classified = YtDlpErrorClassifier.Classify(lastRunResult.StandardError, lastRunResult.ExitCode);
            var attemptsUsed = Math.Max(transientAttempt, reprobeAttempt);
            await RecordFailureAndPersistAsync(
                runtime, classified.Category.ToString(), classified.UserMessage,
                classified.TechnicalSummary, lastRunResult.ExitCode, attemptsUsed).ConfigureAwait(false);

            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Failed, 0, false, lastDownloadedBytes, null, null, null, lastRecordedDuration,
                "Recording failed", ErrorMessage: classified.UserMessage, TechnicalDetails: classified.TechnicalSummary));
            return false;
        }

        // Remux crash-safe MKV/TS -> MP4 (+faststart) INSIDE jobTempDir so user Save folder never sees intermediate files
        var workingOutputPath = capturedFile!;
        if (activeReq.OutputContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            && !workingOutputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Finalizing, 98, true, lastDownloadedBytes, null, null, null, lastRecordedDuration,
                "Finalizing MP4 container (faststart)...", NonFatalNotice: latestNotice));

            var tempTargetMp4 = Path.Combine(jobTempDir, $"{reservation.RelativeBaseFileName}.mp4");
            var remuxRes = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.FFmpegPath,
                Arguments = YtDlpArgumentBuilder.BuildRemuxToMp4Arguments(workingOutputPath, tempTargetMp4),
                GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(5)
            }, CancellationToken.None).ConfigureAwait(false);

            if (remuxRes.ExitCode == 0 && File.Exists(tempTargetMp4) && new FileInfo(tempTargetMp4).Length > 0)
            {
                try { File.Delete(workingOutputPath); } catch { }
                workingOutputPath = tempTargetMp4;
            }
            else
            {
                latestNotice = "Recording saved safely in MKV/TS format (MP4 remux skipped to preserve stream codecs).";
            }
        }

        // Move finalized playable recording safely from jobTempDir into user's Save folder (cross-volume safe)
        var finalExtension = Path.GetExtension(workingOutputPath);
        if (string.IsNullOrWhiteSpace(finalExtension))
            finalExtension = ".mkv";
        var preferredFinalName = $"{reservation.RelativeBaseFileName}{finalExtension}";

        string finalOutputPath;
        try
        {
            finalOutputPath = await OutputPathGuard.MoveFileCrossVolumeSafeAsync(
                workingOutputPath, reservation.OutputDirectory, preferredFinalName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move finalized livestream recording '{Src}' into '{Dest}'.", workingOutputPath, reservation.OutputDirectory);
            var errMsg = $"Recording finished, but moving it to '{reservation.OutputDirectory}' failed: {ex.Message}";
            await RecordFailureAndPersistAsync(runtime, DownloadErrorCategory.DiskOrPermission.ToString(), errMsg, $"Recording preserved safely at: {workingOutputPath}", null, 0).ConfigureAwait(false);
            Report(runtime, new DownloadProgressSnapshot(
                activeReq.JobId, DownloadJobState.Failed, 99, false, lastDownloadedBytes, null, null, null, lastRecordedDuration,
                "Failed to move recording",
                ErrorMessage: errMsg,
                TechnicalDetails: $"Recording preserved safely at: {workingOutputPath}"));
            return true; // Preserve jobTempDir so the recording is NEVER lost!
        }

        Report(runtime, new DownloadProgressSnapshot(
            activeReq.JobId, DownloadJobState.Finalizing, 99, true, lastDownloadedBytes, null, null, null, lastRecordedDuration,
            "Verifying recording integrity...", NonFatalNotice: latestNotice));

        var integrity = await VerifyOutputSafeAsync(toolchain, finalOutputPath, activeReq, jobTempDir).ConfigureAwait(false);
        var finalSize = File.Exists(finalOutputPath) ? new FileInfo(finalOutputPath).Length : integrity.FileSizeBytes;

        await PersistHistorySafeAsync(activeReq, finalOutputPath, finalSize, integrity).ConfigureAwait(false);
        await _jobRepository.UpdateJobStateAsync(
            activeReq.JobId,
            DownloadJobState.Completed,
            progressPercent: 100,
            downloadedBytes: finalSize,
            totalBytes: finalSize,
            outputFilePath: finalOutputPath,
            clearError: true,
            ct: CancellationToken.None).ConfigureAwait(false);

        TryDeleteDirectoryWithRetry(jobTempDir);

        var completionLabel = runtime.UserCancelled
            ? "Recording stopped & saved"
            : (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge || modeCStopTriggered
                ? "Historical stream captured"
                : "Recording completed");

        var resolvedDuration = integrity.DurationSeconds is > 0
            ? TimeSpan.FromSeconds(integrity.DurationSeconds.Value)
            : lastRecordedDuration;

        Report(runtime, new DownloadProgressSnapshot(
            activeReq.JobId, DownloadJobState.Completed, 100, false, finalSize, finalSize, null, TimeSpan.Zero,
            resolvedDuration, completionLabel, OutputFilePath: finalOutputPath, NonFatalNotice: latestNotice,
            IntegrityStatus: integrity.Status));

        return false;
    }

    private async Task WaitForRetryDelayAsync(
        JobRuntimeState runtime,
        TimeSpan delay,
        double currentPercent,
        long? downloadedBytes,
        long? totalBytes,
        int attempt,
        int maxAttempts,
        Func<int, string> phaseBuilder,
        CancellationToken ct)
    {
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
        Report(runtime, new DownloadProgressSnapshot(
            runtime.Request.JobId, DownloadJobState.Retrying, currentPercent, false,
            downloadedBytes, totalBytes, null, null, null,
            phaseBuilder(totalSeconds),
            RetryAttempt: attempt, MaxRetryAttempts: maxAttempts));

        if (_customRetryDelayAsync is not null)
        {
            await _customRetryDelayAsync(delay, ct).ConfigureAwait(false);
            return;
        }

        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            await Task.Delay(step, ct).ConfigureAwait(false);
            remaining -= step;

            if (remaining > TimeSpan.Zero)
            {
                var secsLeft = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                Report(runtime, new DownloadProgressSnapshot(
                    runtime.Request.JobId, DownloadJobState.Retrying, currentPercent, false,
                    downloadedBytes, totalBytes, null, null, null,
                    phaseBuilder(secsLeft),
                    RetryAttempt: attempt, MaxRetryAttempts: maxAttempts));
            }
        }
    }

    private async Task<DownloadJobRequest?> TryReprobeAndRefreshRequestAsync(DownloadJobRequest current, CancellationToken ct)
    {
        try
        {
            var probe = await _mediaProbeService.ProbeAsync(current.SourceUrl, ct).ConfigureAwait(false);
            var match = probe.AvailableQualities.FirstOrDefault(q =>
                            string.Equals(q.Id, current.SelectedQuality.Id, StringComparison.OrdinalIgnoreCase))
                        ?? probe.AvailableQualities
                            .Where(q => q.IsAudioOnly == current.SelectedQuality.IsAudioOnly)
                            .OrderBy(q => Math.Abs((q.Height ?? 0) - (current.SelectedQuality.Height ?? 0)))
                            .FirstOrDefault()
                        ?? probe.AvailableQualities.FirstOrDefault();

            if (match is null)
                return null;

            var updatedReq = current with
            {
                SelectedQuality = match,
                KnownDurationSeconds = probe.DurationSeconds ?? current.KnownDurationSeconds,
                IsDvrAvailable = probe.DvrInfo?.IsDvrAvailable ?? current.IsDvrAvailable,
                HasMuxedHlsFromStart = probe.DvrInfo?.HasMuxedHlsFromStart ?? current.HasMuxedHlsFromStart
            };

            await _jobRepository.UpdateQualityAndEstimatedBytesAsync(
                current.JobId,
                PersistedDownloadJob.SerializeQualityOption(match),
                match.EstimatedSizeBytes,
                CancellationToken.None).ConfigureAwait(false);

            return updatedReq;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic re-probe before retry failed for job {JobId}; retrying with existing metadata.", current.JobId);
            return null;
        }
    }

    private async Task<MediaIntegrityResult> VerifyOutputSafeAsync(
        ResolvedToolchain toolchain,
        string outputFilePath,
        DownloadJobRequest req,
        string jobTempDir)
    {
        try
        {
            return await _mediaIntegrityService.VerifyAndRepairIfNeededAsync(
                toolchain,
                outputFilePath,
                expectVideo: !req.SelectedQuality.IsAudioOnly,
                expectAudio: true,
                workingTempDirectory: jobTempDir,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-download integrity check failed unexpectedly for '{File}'.", outputFilePath);
            var size = File.Exists(outputFilePath) ? new FileInfo(outputFilePath).Length : 0;
            return new MediaIntegrityResult(
                MediaIntegrityStatus.Unverified,
                $"Integrity check skipped: {ex.Message}",
                size,
                req.KnownDurationSeconds,
                null, null, null,
                WasRepaired: false,
                VerifiedAtUtc: DateTimeOffset.UtcNow);
        }
    }

    private async Task HandleJobPausedAsync(JobRuntimeState runtime, double pct, long? downloadedBytes, long? totalBytes)
    {
        await _jobRepository.UpdateJobStateAsync(
            runtime.Request.JobId,
            DownloadJobState.Paused,
            progressPercent: pct,
            downloadedBytes: downloadedBytes,
            totalBytes: totalBytes,
            lastErrorMessage: "Paused — ready to resume",
            ct: CancellationToken.None).ConfigureAwait(false);

        Report(runtime, new DownloadProgressSnapshot(
            runtime.Request.JobId,
            DownloadJobState.Paused,
            pct, false, downloadedBytes, totalBytes, null, null, null,
            "Paused — ready to resume"));
    }

    private async Task HandleJobCancelledAsync(JobRuntimeState runtime, string jobTempDir)
    {
        await _jobRepository.UpdateJobStateAsync(
            runtime.Request.JobId,
            DownloadJobState.Cancelled,
            clearError: true,
            ct: CancellationToken.None).ConfigureAwait(false);

        TryDeleteDirectoryWithRetry(jobTempDir);

        Report(runtime, new DownloadProgressSnapshot(
            runtime.Request.JobId,
            DownloadJobState.Cancelled,
            0, false, null, null, null, null, null,
            "Cancelled"));
    }

    private async Task RecordFailureAndPersistAsync(
        JobRuntimeState runtime,
        string category,
        string userMessage,
        string technicalDetails,
        int? exitCode,
        int retryAttempt)
    {
        await _jobRepository.UpdateJobStateAsync(
            runtime.Request.JobId,
            DownloadJobState.Failed,
            retryAttempt: retryAttempt,
            lastErrorCategory: category,
            lastErrorMessage: userMessage,
            lastTechnicalDetails: technicalDetails,
            ct: CancellationToken.None).ConfigureAwait(false);

        try
        {
            await _diagnosticsRepository.RecordFailureAsync(new DiagnosticEventRecord
            {
                JobId = runtime.Request.JobId,
                VideoId = runtime.Request.VideoId,
                Title = runtime.Request.Title,
                ErrorCategory = category,
                UserMessage = userMessage,
                RedactedTechnicalDetails = technicalDetails,
                ExitCode = exitCode,
                RetryAttempt = retryAttempt,
                OccurredAtUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist diagnostic event for failed job {JobId}.", runtime.Request.JobId);
        }
    }

    private async Task<string?> TryManualMuxPartialTracksAsync(
        ResolvedToolchain toolchain,
        string baseName,
        string jobTempDir,
        string? searchOutputDir)
    {
        try
        {
            var searchDirs = new[] { jobTempDir, searchOutputDir }
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in searchDirs)
            {
                var parts = Directory.EnumerateFiles(dir, "*.f*")
                    .Where(f => !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                                && !f.Contains("-Frag", StringComparison.OrdinalIgnoreCase)
                                && IsAnySplitFormatTrackFile(Path.GetFileName(f))
                                && new FileInfo(f).Length > 0)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToList();

                if (parts.Count < 2)
                    continue;

                var outputMkv = Path.Combine(jobTempDir, $"{baseName}.mkv");
                var res = await _processRunner.RunAsync(new ProcessRunRequest
                {
                    ExecutablePath = toolchain.FFmpegPath,
                    Arguments = YtDlpArgumentBuilder.BuildManualMuxFallbackArguments(parts[0], parts[1], outputMkv),
                    GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                }, CancellationToken.None).ConfigureAwait(false);

                if (res.ExitCode == 0 && File.Exists(outputMkv) && new FileInfo(outputMkv).Length > 0)
                {
                    return outputMkv;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback manual FFmpeg mux of partial livestream tracks failed.");
        }

        return null;
    }

    public static TimeSpan? CalculateModeCTargetDuration(DateTimeOffset? streamStartUtc, int liveEdgeMarginSeconds, DateTimeOffset? nowUtc = null)
    {
        if (!streamStartUtc.HasValue)
            return null;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var elapsed = now - streamStartUtc.Value;
        if (elapsed <= TimeSpan.Zero)
            return null;

        var maxDvrWindow = TimeSpan.FromHours(120);
        var availableHistory = elapsed > maxDvrWindow ? maxDvrWindow : elapsed;
        var margin = TimeSpan.FromSeconds(Math.Clamp(liveEdgeMarginSeconds, 15, 300));
        var target = availableHistory - margin;

        return target >= TimeSpan.FromSeconds(15) ? target : null;
    }

    private static bool HasRecoverableLivestreamMedia(string? tempDir)
    {
        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
            return false;

        try
        {
            return Directory.EnumerateFiles(tempDir)
                .Any(f =>
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("-Frag", StringComparison.OrdinalIgnoreCase))
                        return false;

                    var ext = Path.GetExtension(name);
                    var isMedia = ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                                  || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                                  || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
                                  || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                                  || ext.Equals(".part", StringComparison.OrdinalIgnoreCase);

                    return isMedia && new FileInfo(f).Length > 0;
                });
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPartialFilesInDirectory(string tempDir)
    {
        try
        {
            return Directory.Exists(tempDir) && Directory.EnumerateFiles(tempDir).Any(f => new FileInfo(f).Length > 0);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExistingCompleteMediaInTemp(string tempDir)
    {
        return Directory.EnumerateFiles(tempDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(name);
                return (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase))
                       && !IsAnySplitFormatTrackFile(name)
                       && new FileInfo(f).Length > 0;
            })
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static string? FindLargestSinglePartialMediaFile(string tempDir)
    {
        return Directory.EnumerateFiles(tempDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                       && !name.Contains("-Frag", StringComparison.OrdinalIgnoreCase)
                       && (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                       && new FileInfo(f).Length > 0;
            })
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static string? ResolveFinalOutputFile(
        string? printedFinal,
        string? lastDestination,
        OutputPathReservation reservation,
        string jobTempDir,
        bool allowPartFallback)
    {
        if (!string.IsNullOrWhiteSpace(printedFinal) && File.Exists(printedFinal))
            return printedFinal;

        if (!string.IsNullOrWhiteSpace(lastDestination)
            && File.Exists(lastDestination)
            && !IsSplitFormatTrackFile(Path.GetFileName(lastDestination), reservation.RelativeBaseFileName))
        {
            return lastDestination;
        }

        if (File.Exists(reservation.TargetFilePath))
            return reservation.TargetFilePath;

        var baseName = reservation.RelativeBaseFileName;
        var searchDirs = new[] { reservation.OutputDirectory, jobTempDir }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in searchDirs)
        {
            var candidates = Directory.EnumerateFiles(dir, $"{baseName}.*")
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                            && !f.Contains("-Frag", StringComparison.OrdinalIgnoreCase)
                            && !IsSplitFormatTrackFile(Path.GetFileName(f), baseName))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];
        }

        if (!allowPartFallback)
            return null;

        foreach (var dir in searchDirs)
        {
            var partFile = Directory.EnumerateFiles(dir, $"{baseName}.*.part")
                .Where(f => !f.Contains("-Frag", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(partFile) && new FileInfo(partFile).Length > 0)
            {
                var promotedPath = partFile[..^5]; // strip ".part"
                try
                {
                    File.Move(partFile, promotedPath, overwrite: true);
                    return promotedPath;
                }
                catch
                {
                    return partFile;
                }
            }
        }

        return null;
    }

    private static bool IsSplitFormatTrackFile(string fileName, string baseName)
    {
        if (!fileName.StartsWith(baseName + ".f", StringComparison.OrdinalIgnoreCase))
            return false;

        var afterF = baseName.Length + 2;
        return afterF < fileName.Length && char.IsDigit(fileName[afterF]);
    }

    private static bool IsAnySplitFormatTrackFile(string fileName)
    {
        var idx = fileName.IndexOf(".f", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var digitIdx = idx + 2;
            if (digitIdx < fileName.Length && char.IsDigit(fileName[digitIdx]))
            {
                // Ensure followed by digits and another dot (e.g., .f134.mp4.part or .f251.webm)
                var scan = digitIdx;
                while (scan < fileName.Length && char.IsDigit(fileName[scan]))
                    scan++;
                if (scan < fileName.Length && fileName[scan] == '.')
                    return true;
            }

            idx = fileName.IndexOf(".f", idx + 2, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private string BuildJobTempDirectory(string jobId)
        => Path.Combine(_paths.TempDownloadsDirectory, OutputPathGuard.SanitizeFileNameSegment(jobId, 48));

    private static void TryDeleteDirectoryWithRetry(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
            catch
            {
                return;
            }
        }
    }

    private async Task PersistHistorySafeAsync(
        DownloadJobRequest req,
        string? outputFile,
        long fileSize,
        MediaIntegrityResult? integrity)
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
                Resolution = integrity?.Resolution,
                VideoCodec = integrity?.VideoCodec,
                AudioCodec = integrity?.AudioCodec,
                FileSizeBytes = fileSize,
                DurationSeconds = integrity?.DurationSeconds ?? req.KnownDurationSeconds,
                WasLivestream = req.LiveStatus is MediaLiveStatus.IsLive or MediaLiveStatus.WasLive,
                LivestreamMode = req.LivestreamMode,
                IntegrityStatus = integrity?.Status ?? MediaIntegrityStatus.Unverified,
                IntegrityMessage = integrity?.Message,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                VerifiedAtUtc = integrity?.VerifiedAtUtc
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist download history entry.");
        }
    }

    private void Report(JobRuntimeState runtime, DownloadProgressSnapshot snapshot)
        => EmitProgress(runtime.Request.JobId, snapshot);

    private void EmitProgress(string jobId, DownloadProgressSnapshot snapshot)
    {
        if (_progressSubscribers.TryGetValue(jobId, out var subscriber))
        {
            try { subscriber.Report(snapshot); } catch { }
        }

        try { JobProgressChanged?.Invoke(snapshot); } catch { }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(8));
        }
        catch
        {
            // Ensure shutdown cancellation never throws during synchronous container teardown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await CancelAllAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _dispatcherTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown timeout.
        }

        _shutdownCts.Dispose();
        _wakeSignal.Dispose();
    }

    private sealed class JobRuntimeState(
        PersistedDownloadJob persisted,
        DownloadJobRequest request,
        CancellationTokenSource cts)
    {
        public PersistedDownloadJob Persisted { get; } = persisted;
        public DownloadJobRequest Request { get; } = request;
        public CancellationTokenSource Cts { get; } = cts;
        public bool UserCancelled { get; set; }
        public bool UserPaused { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
