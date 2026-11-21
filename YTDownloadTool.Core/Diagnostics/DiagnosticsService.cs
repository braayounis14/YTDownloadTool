using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Diagnostics;

public sealed record TempStorageSummary(
    long TotalBytes,
    long ReclaimableUnusedBytes,
    long ProtectedBytes,
    int UnusedFolderCount,
    int ProtectedFolderCount,
    bool CanSafelyPrune);

public sealed record TempCleanupResult(
    int DeletedFolders,
    long ReclaimedBytes,
    bool SkippedDueToRecoveryMode);

public sealed record DiagnosticsSummary(
    DatabaseHealthSnapshot DatabaseHealth,
    int RecentFailureCount,
    TempStorageSummary TempStorage,
    DateTimeOffset CapturedAtUtc);

public sealed record DiagnosticExportResult(
    string ZipFilePath,
    int IncludedLogFiles,
    int IncludedFailedJobs,
    long ZipSizeBytes);

public interface IDiagnosticsService
{
    Task<DiagnosticsSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<TempCleanupResult> CleanUnusedTempAsync(CancellationToken ct = default);
    Task<DiagnosticExportResult> ExportDiagnosticBundleAsync(string destinationZipPath, string appVersion, CancellationToken ct = default);
}

public sealed class DiagnosticsService : IDiagnosticsService
{
    private const int ExportedFailedJobsLimit = 25;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ISqliteDatabaseManager _dbManager;
    private readonly IDownloadJobRepository _jobRepository;
    private readonly IDiagnosticsRepository _diagnosticsRepository;
    private readonly ISettingsStore _settingsStore;
    private readonly IToolManager _toolManager;
    private readonly IDiskSpaceReservationLedger _diskLedger;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(
        IAppPaths paths,
        ISqliteDatabaseManager dbManager,
        IDownloadJobRepository jobRepository,
        IDiagnosticsRepository diagnosticsRepository,
        ISettingsStore settingsStore,
        IToolManager toolManager,
        IDiskSpaceReservationLedger diskLedger,
        ILogger<DiagnosticsService> logger)
    {
        _paths = paths;
        _dbManager = dbManager;
        _jobRepository = jobRepository;
        _diagnosticsRepository = diagnosticsRepository;
        _settingsStore = settingsStore;
        _toolManager = toolManager;
        _diskLedger = diskLedger;
        _logger = logger;
    }

    public async Task<DiagnosticsSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await _dbManager.InitializeAsync(ct).ConfigureAwait(false);
        var dbHealth = _dbManager.RefreshHealthSnapshot();
        var failureCount = await _diagnosticsRepository.GetFailureCountAsync(ct).ConfigureAwait(false);
        var tempStorage = await InspectTempStorageAsync(ct).ConfigureAwait(false);

        return new DiagnosticsSummary(
            DatabaseHealth: dbHealth,
            RecentFailureCount: failureCount,
            TempStorage: tempStorage,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Cleans ONLY unused temp folders under %LocalAppData%\YTDownloadToolData\Temp\ using the exact same
    /// GetProtectedTempDirectoryNamesAsync() guard as startup pruning so paused, queued, active, retrying,
    /// and recoverable livestream job folders are NEVER deleted.
    /// </summary>
    public async Task<TempCleanupResult> CleanUnusedTempAsync(CancellationToken ct = default)
    {
        var protectedSnap = await _jobRepository.GetProtectedTempDirectoryNamesAsync(ct).ConfigureAwait(false);
        if (!protectedSnap.CanSafelyPrune)
        {
            _logger.LogWarning("Skipping Settings Clean Unused Temp because SQLite database is in recovery mode.");
            return new TempCleanupResult(0, 0, SkippedDueToRecoveryMode: true);
        }

        if (!Directory.Exists(_paths.TempDownloadsDirectory))
            return new TempCleanupResult(0, 0, SkippedDueToRecoveryMode: false);

        var deletedFolders = 0;
        long reclaimedBytes = 0;

        foreach (var dir in Directory.EnumerateDirectories(_paths.TempDownloadsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName)
                || protectedSnap.ProtectedFolderNames.Contains(folderName))
            {
                continue;
            }

            var size = CalculateDirectoryBytesSafe(dir);
            if (TryDeleteDirectorySafe(dir))
            {
                deletedFolders++;
                reclaimedBytes += size;
            }
        }

        // Also clean any loose top-level orphan files directly under TempDownloadsDirectory
        foreach (var file in Directory.EnumerateFiles(_paths.TempDownloadsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                reclaimedBytes += len;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete loose temp file '{File}'.", file);
            }
        }

        return new TempCleanupResult(deletedFolders, reclaimedBytes, SkippedDueToRecoveryMode: false);
    }

    public async Task<DiagnosticExportResult> ExportDiagnosticBundleAsync(
        string destinationZipPath,
        string appVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var fullDest = Path.GetFullPath(destinationZipPath);
        var parentDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrWhiteSpace(parentDir))
            Directory.CreateDirectory(parentDir);

        var summary = await GetSummaryAsync(ct).ConfigureAwait(false);
        var failedJobs = await _diagnosticsRepository.GetRecentFailuresAsync(ExportedFailedJobsLimit, ct).ConfigureAwait(false);

        IReadOnlyList<ToolStatusInfo> toolStatuses = [];
        try
        {
            toolStatuses = await _toolManager.GetStatusesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read tool statuses for diagnostic export.");
        }

        var tempZipPath = $"{fullDest}.{Guid.NewGuid():N}.tmp";
        var includedLogs = 0;

        try
        {
            using (var zipStream = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. Redacted log files
                if (Directory.Exists(_paths.LogsDirectory))
                {
                    var logFiles = Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Take(10)
                        .ToList();

                    foreach (var logFile in logFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        var redactedContent = await ReadAndRedactTextFileAsync(logFile, ct).ConfigureAwait(false);
                        await WriteTextEntryAsync(archive, $"logs/{Path.GetFileName(logFile)}", redactedContent, ct).ConfigureAwait(false);
                        includedLogs++;
                    }
                }

                // 2. Tools manifest.json + probed tool versions
                if (File.Exists(_paths.ToolsManifestPath))
                {
                    var manifestRedacted = await ReadAndRedactTextFileAsync(_paths.ToolsManifestPath, ct).ConfigureAwait(false);
                    await WriteTextEntryAsync(archive, "tools/manifest.json", manifestRedacted, ct).ConfigureAwait(false);
                }

                var toolVersionsPayload = toolStatuses.Select(t => new
                {
                    kind = t.Kind.ToString(),
                    displayName = t.DisplayName,
                    state = t.State.ToString(),
                    installedVersion = t.InstalledVersion,
                    previousVersion = t.PreviousVersion,
                    availableVersion = t.AvailableVersion,
                    updateAvailable = t.UpdateAvailable,
                    canRollback = t.CanRollback,
                    executableName = string.IsNullOrWhiteSpace(t.ExecutablePath) ? null : Path.GetFileName(t.ExecutablePath)
                });
                await WriteTextEntryAsync(
                    archive,
                    "tools/versions.json",
                    JsonSerializer.Serialize(toolVersionsPayload, PrettyJsonOptions),
                    ct).ConfigureAwait(false);

                // 3. system-info.json
                var systemInfo = BuildSystemInfoPayload(appVersion, summary);
                await WriteTextEntryAsync(
                    archive,
                    "system-info.json",
                    JsonSerializer.Serialize(systemInfo, PrettyJsonOptions),
                    ct).ConfigureAwait(false);

                // 4. Sanitized last 25 failed jobs
                var sanitizedFailures = failedJobs.Select(f => new
                {
                    f.Id,
                    f.JobId,
                    f.VideoId,
                    title = SensitiveDataRedactor.Redact(f.Title),
                    f.ErrorCategory,
                    userMessage = SensitiveDataRedactor.Redact(f.UserMessage),
                    technicalDetails = SensitiveDataRedactor.Redact(f.RedactedTechnicalDetails),
                    f.ExitCode,
                    f.RetryAttempt,
                    f.OccurredAtUtc
                }).ToList();

                await WriteTextEntryAsync(
                    archive,
                    "failed-jobs.json",
                    JsonSerializer.Serialize(sanitizedFailures, PrettyJsonOptions),
                    ct).ConfigureAwait(false);
            }

            File.Move(tempZipPath, fullDest, overwrite: true);
            var zipSize = File.Exists(fullDest) ? new FileInfo(fullDest).Length : 0;

            _logger.LogInformation(
                "Exported diagnostic bundle to '{ZipPath}' ({Logs} log files, {Failures} failed job summaries, {Bytes} bytes).",
                fullDest, includedLogs, failedJobs.Count, zipSize);

            return new DiagnosticExportResult(fullDest, includedLogs, failedJobs.Count, zipSize);
        }
        catch
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            throw;
        }
    }

    private async Task<TempStorageSummary> InspectTempStorageAsync(CancellationToken ct)
    {
        var protectedSnap = await _jobRepository.GetProtectedTempDirectoryNamesAsync(ct).ConfigureAwait(false);

        if (!Directory.Exists(_paths.TempDownloadsDirectory))
        {
            return new TempStorageSummary(0, 0, 0, 0, 0, protectedSnap.CanSafelyPrune);
        }

        long protectedBytes = 0;
        long unusedBytes = 0;
        var protectedCount = 0;
        var unusedCount = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_paths.TempDownloadsDirectory))
            {
                var folderName = Path.GetFileName(dir);
                var bytes = CalculateDirectoryBytesSafe(dir);

                if (!protectedSnap.CanSafelyPrune || protectedSnap.ProtectedFolderNames.Contains(folderName))
                {
                    protectedBytes += bytes;
                    protectedCount++;
                }
                else
                {
                    unusedBytes += bytes;
                    unusedCount++;
                }
            }

            foreach (var file in Directory.EnumerateFiles(_paths.TempDownloadsDirectory))
            {
                try
                {
                    var len = new FileInfo(file).Length;
                    if (protectedSnap.CanSafelyPrune)
                        unusedBytes += len;
                    else
                        protectedBytes += len;
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error inspecting temp storage size.");
        }

        return new TempStorageSummary(
            TotalBytes: protectedBytes + unusedBytes,
            ReclaimableUnusedBytes: unusedBytes,
            ProtectedBytes: protectedBytes,
            UnusedFolderCount: unusedCount,
            ProtectedFolderCount: protectedCount,
            CanSafelyPrune: protectedSnap.CanSafelyPrune);
    }

    private object BuildSystemInfoPayload(string appVersion, DiagnosticsSummary summary)
    {
        var cfg = _settingsStore.Current;
        var relevantPaths = new[]
        {
            _paths.DataRootDirectory,
            _paths.SettingsRootDirectory,
            _paths.DefaultDownloadsDirectory,
            cfg.DownloadDirectory
        };

        var driveRoots = relevantPaths
            .Select(DiskSpaceReservationLedger.TryNormalizeDriveRoot)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var driveSummaries = new List<object>();
        foreach (var root in driveRoots)
        {
            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    continue;

                driveSummaries.Add(new
                {
                    drive = drive.Name,
                    format = drive.DriveFormat,
                    availableFreeBytes = drive.AvailableFreeSpace,
                    totalSizeBytes = drive.TotalSize
                });
            }
            catch
            {
            }
        }

        return new
        {
            appVersion = string.IsNullOrWhiteSpace(appVersion) ? "1.1.1" : appVersion,
            exportedAtUtc = DateTimeOffset.UtcNow,
            osDescription = RuntimeInformation.OSDescription,
            osVersion = Environment.OSVersion.VersionString,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            frameworkDescription = RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            settings = new
            {
                cfg.DefaultQuality,
                cfg.DefaultContainer,
                cfg.MaxConcurrentDownloads,
                cfg.PerDownloadSpeedLimitKbps,
                cfg.DefaultLivestreamMode,
                cfg.LiveEdgeMarginSeconds,
                cfg.YtDlpChannel,
                cfg.AutoUpdateTools,
                cfg.AutoCheckAppUpdates
            },
            database = new
            {
                status = summary.DatabaseHealth.Status.ToString(),
                summary.DatabaseHealth.CurrentSchemaVersion,
                summary.DatabaseHealth.TargetSchemaVersion,
                summary.DatabaseHealth.IsSchemaCompatible,
                summary.DatabaseHealth.DatabaseSizeBytes,
                summary.DatabaseHealth.BackupCount,
                summary.DatabaseHealth.RecoveryNotice
            },
            tempStorage = summary.TempStorage,
            recentFailureCount = summary.RecentFailureCount,
            drives = driveSummaries,
            activeDiskReservations = _diskLedger.GetDriveReservationsSnapshot()
        };
    }

    private static async Task<string> ReadAndRedactTextFileAsync(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            return SensitiveDataRedactor.Redact(raw);
        }
        catch (Exception ex)
        {
            return $"[Could not read file: {ex.Message}]";
        }
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await entryStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static long CalculateDirectoryBytesSafe(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch
        {
        }

        return total;
    }

    private static bool TryDeleteDirectorySafe(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
