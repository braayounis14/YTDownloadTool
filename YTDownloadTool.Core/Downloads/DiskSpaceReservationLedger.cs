using System.Globalization;

namespace YTDownloadTool.Core.Downloads;

public sealed record DiskSpaceReservationResult(
    bool Succeeded,
    string? FailureMessage = null,
    string? DriveRoot = null,
    long RequiredBytes = 0,
    long AvailableBytes = 0,
    long AlreadyReservedBytes = 0);

public interface IDiskSpaceReservationLedger
{
    DiskSpaceReservationResult TryReserve(
        string jobId,
        string tempDirectory,
        string destinationDirectory,
        long? estimatedSizeBytes,
        bool isLivestream);

    void Release(string jobId);
    long GetReservedBytesForDrive(string pathOrDriveRoot);
    IReadOnlyDictionary<string, long> GetDriveReservationsSnapshot();
}

/// <summary>
/// Thread-safe shared per-volume disk-space reservation ledger across all Running and Queued jobs.
/// Accounts for both temporary staging and final destination volumes, merge headroom, an OS safety
/// margin, and fallback floors for unknown-size VODs and livestreams.
/// </summary>
public sealed class DiskSpaceReservationLedger : IDiskSpaceReservationLedger
{
    public const long DefaultUnknownVodEstimateBytes = 512L * 1024 * 1024;           // 512 MB
    public const long DefaultUnknownLivestreamEstimateBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    public const long OsSafetyMarginBytes = 200L * 1024 * 1024;                      // 200 MB

    public const double SameVolumeMultiplier = 2.2;
    public const double SeparateTempMultiplier = 2.0;
    public const double SeparateDestinationMultiplier = 1.15;

    private readonly Func<string, long?> _freeSpaceProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, long>> _reservationsByJob = new(StringComparer.Ordinal);

    public DiskSpaceReservationLedger(Func<string, long?>? freeSpaceProvider = null)
    {
        _freeSpaceProvider = freeSpaceProvider ?? DefaultFreeSpaceProvider;
    }

    public DiskSpaceReservationResult TryReserve(
        string jobId,
        string tempDirectory,
        string destinationDirectory,
        long? estimatedSizeBytes,
        bool isLivestream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var effectiveEstimate = estimatedSizeBytes is > 0
            ? estimatedSizeBytes.Value
            : (isLivestream ? DefaultUnknownLivestreamEstimateBytes : DefaultUnknownVodEstimateBytes);

        var tempRoot = TryNormalizeDriveRoot(tempDirectory);
        var destRoot = TryNormalizeDriveRoot(destinationDirectory);

        var requiredByDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (tempRoot is not null && destRoot is not null && tempRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
        {
            requiredByDrive[tempRoot] = (long)Math.Ceiling(effectiveEstimate * SameVolumeMultiplier);
        }
        else
        {
            if (tempRoot is not null)
                requiredByDrive[tempRoot] = (long)Math.Ceiling(effectiveEstimate * SeparateTempMultiplier);
            if (destRoot is not null)
                requiredByDrive[destRoot] = (long)Math.Ceiling(effectiveEstimate * SeparateDestinationMultiplier);
        }

        lock (_lock)
        {
            foreach (var (driveRoot, jobBytes) in requiredByDrive)
            {
                var available = _freeSpaceProvider(driveRoot);
                if (!available.HasValue)
                    continue; // Non-local / UNC path: skip gracefully

                var otherReserved = SumReservedForDriveLocked(driveRoot, excludeJobId: jobId);
                var totalRequired = otherReserved + jobBytes + OsSafetyMarginBytes;

                if (available.Value < totalRequired)
                {
                    var detail = otherReserved > 0
                        ? $"Not enough free space on drive {driveRoot} after accounting for active/queued downloads ({FormatBytes(otherReserved)} reserved). Required: {FormatBytes(totalRequired)}, Available: {FormatBytes(available.Value)}."
                        : $"Not enough free space on drive {driveRoot}. Required: {FormatBytes(totalRequired)}, Available: {FormatBytes(available.Value)}.";

                    return new DiskSpaceReservationResult(
                        Succeeded: false,
                        FailureMessage: detail,
                        DriveRoot: driveRoot,
                        RequiredBytes: totalRequired,
                        AvailableBytes: available.Value,
                        AlreadyReservedBytes: otherReserved);
                }
            }

            _reservationsByJob[jobId] = requiredByDrive;
            return new DiskSpaceReservationResult(Succeeded: true);
        }
    }

    public void Release(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;

        lock (_lock)
        {
            _reservationsByJob.Remove(jobId);
        }
    }

    public long GetReservedBytesForDrive(string pathOrDriveRoot)
    {
        var root = TryNormalizeDriveRoot(pathOrDriveRoot);
        if (root is null)
            return 0;

        lock (_lock)
        {
            return SumReservedForDriveLocked(root, excludeJobId: null);
        }
    }

    public IReadOnlyDictionary<string, long> GetDriveReservationsSnapshot()
    {
        lock (_lock)
        {
            var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var jobMap in _reservationsByJob.Values)
            {
                foreach (var (drive, bytes) in jobMap)
                {
                    totals[drive] = totals.TryGetValue(drive, out var existing) ? existing + bytes : bytes;
                }
            }

            return totals;
        }
    }

    private long SumReservedForDriveLocked(string driveRoot, string? excludeJobId)
    {
        long total = 0;
        foreach (var (jobId, jobMap) in _reservationsByJob)
        {
            if (excludeJobId is not null && string.Equals(jobId, excludeJobId, StringComparison.Ordinal))
                continue;

            if (jobMap.TryGetValue(driveRoot, out var bytes))
                total += bytes;
        }

        return total;
    }

    public static string? TryNormalizeDriveRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return null;

            // Skip UNC paths (\\server\share) since DriveInfo does not support them reliably
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;

            return root.ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static long? DefaultFreeSpaceProvider(string driveRoot)
    {
        try
        {
            var drive = new DriveInfo(driveRoot);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        return bytes switch
        {
            >= (long)gb => string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", bytes / gb),
            >= (long)mb => string.Format(CultureInfo.InvariantCulture, "{0:F0} MB", bytes / mb),
            >= (long)kb => string.Format(CultureInfo.InvariantCulture, "{0:F0} KB", bytes / kb),
            _ => string.Format(CultureInfo.InvariantCulture, "{0} B", bytes)
        };
    }
}
