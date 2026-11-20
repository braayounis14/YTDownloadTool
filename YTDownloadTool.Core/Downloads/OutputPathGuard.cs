using System.Text;

namespace YTDownloadTool.Core.Downloads;

public sealed class OutputPathReservation : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal OutputPathReservation(string outputDirectory, string relativeBaseFileName, string reservedBasePath, string targetFilePath, Action release)
    {
        OutputDirectory = outputDirectory;
        RelativeBaseFileName = relativeBaseFileName;
        ReservedBasePath = reservedBasePath;
        TargetFilePath = targetFilePath;
        _release = release;
    }

    public string OutputDirectory { get; }
    public string RelativeBaseFileName { get; }
    public string ReservedBasePath { get; }
    public string TargetFilePath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}

public sealed class OutputPathGuard
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly Lock _lock = new();
    private readonly HashSet<string> _activeBasePaths = new(StringComparer.OrdinalIgnoreCase);

    public OutputPathReservation ReserveUniqueOutputPath(
        string outputDirectory,
        string title,
        string? videoId,
        string extension,
        string? suffixTag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullOutputDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDir);

        var safeExt = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeExt))
            safeExt = "mp4";

        var sanitizedTitle = SanitizeFileNameSegment(title, maxLength: 100);
        var idPart = string.IsNullOrWhiteSpace(videoId) ? string.Empty : $" [{SanitizeFileNameSegment(videoId, 16)}]";
        var tagPart = string.IsNullOrWhiteSpace(suffixTag) ? string.Empty : $" ({SanitizeFileNameSegment(suffixTag, 24)})";
        var baseName = $"{sanitizedTitle}{tagPart}{idPart}";

        // Ensure total full path stays comfortably inside Windows MAX_PATH (260)
        var maxBaseLen = Math.Max(20, 200 - fullOutputDir.Length - safeExt.Length);
        if (baseName.Length > maxBaseLen)
            baseName = baseName[..maxBaseLen].TrimEnd(' ', '.');

        lock (_lock)
        {
            for (var counter = 1; counter < 1000; counter++)
            {
                var candidateBaseName = counter == 1 ? baseName : $"{baseName} ({counter})";
                var candidateBasePath = Path.GetFullPath(Path.Combine(fullOutputDir, candidateBaseName));
                var candidateFinalPath = $"{candidateBasePath}.{safeExt}";

                if (_activeBasePaths.Contains(candidateBasePath))
                    continue;

                if (File.Exists(candidateFinalPath)
                    || File.Exists($"{candidateBasePath}.mkv")
                    || File.Exists($"{candidateBasePath}.mp4")
                    || File.Exists($"{candidateBasePath}.webm")
                    || File.Exists($"{candidateBasePath}.ts"))
                {
                    continue;
                }

                _activeBasePaths.Add(candidateBasePath);
                return new OutputPathReservation(fullOutputDir, candidateBaseName, candidateBasePath, candidateFinalPath, () =>
                {
                    lock (_lock)
                    {
                        _activeBasePaths.Remove(candidateBasePath);
                    }
                });
            }
        }

        throw new IOException($"Could not reserve a unique filename in '{fullOutputDir}' after 999 attempts.");
    }

    /// <summary>
    /// Checks disk space on both the per-job temporary staging volume and the final user destination volume.
    /// </summary>
    public static void EnsureSufficientDiskSpaceForTempAndDestination(
        string tempDirectory,
        string destinationDirectory,
        long? estimatedSizeBytes)
    {
        var tempRoot = TryGetNormalizedDriveRoot(tempDirectory);
        var destRoot = TryGetNormalizedDriveRoot(destinationDirectory);

        if (tempRoot is not null && destRoot is not null && tempRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Both temp staging & final file share one drive: require ~2.2x headroom for raw streams + merged output
            EnsureSufficientDiskSpace(tempDirectory, estimatedSizeBytes, multiplier: 2.2);
            return;
        }

        // Separate volumes: temp needs ~2.0x (raw streams + merged/remuxed file), destination needs ~1.15x (final file)
        EnsureSufficientDiskSpace(tempDirectory, estimatedSizeBytes, multiplier: 2.0, volumeLabel: "temporary staging");
        EnsureSufficientDiskSpace(destinationDirectory, estimatedSizeBytes, multiplier: 1.15, volumeLabel: "download destination");
    }

    public static void EnsureSufficientDiskSpace(
        string directoryPath,
        long? estimatedSizeBytes,
        double multiplier = 2.2,
        string? volumeLabel = null)
    {
        try
        {
            var root = TryGetNormalizedDriveRoot(directoryPath);
            if (string.IsNullOrWhiteSpace(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;

            const long minFreeBytes = 200L * 1024 * 1024;
            var requiredBytes = estimatedSizeBytes is > 0
                ? Math.Max(minFreeBytes, (long)(estimatedSizeBytes.Value * multiplier))
                : minFreeBytes;

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                var availableMb = drive.AvailableFreeSpace / (1024.0 * 1024.0);
                var requiredMb = requiredBytes / (1024.0 * 1024.0);
                var context = string.IsNullOrWhiteSpace(volumeLabel) ? string.Empty : $" ({volumeLabel})";
                throw new IOException(
                    $"Insufficient disk space on drive {drive.Name}{context}. Available: {availableMb:F0} MB, recommended: {requiredMb:F0} MB.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Non-local / UNC paths where DriveInfo is unsupported: skip pre-check gracefully.
        }
    }

    /// <summary>
    /// Moves a completed file safely from temporary staging into the user's Save directory,
    /// handling both same-volume atomic moves and cross-volume copy-then-rename moves without
    /// overwriting an existing file.
    /// </summary>
    public static async Task<string> MoveFileCrossVolumeSafeAsync(
        string sourceFilePath,
        string destinationDirectory,
        string preferredFileName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredFileName);

        var fullSource = Path.GetFullPath(sourceFilePath);
        var fullDestDir = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestDir);

        // If already in the destination directory, return as-is
        var sourceDir = Path.GetDirectoryName(fullSource);
        if (!string.IsNullOrWhiteSpace(sourceDir) && sourceDir.Equals(fullDestDir, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        var destPath = ResolveNonCollidingDestinationFile(fullDestDir, preferredFileName);
        var sameRoot = string.Equals(
            TryGetNormalizedDriveRoot(fullSource),
            TryGetNormalizedDriveRoot(destPath),
            StringComparison.OrdinalIgnoreCase);

        if (sameRoot)
        {
            try
            {
                File.Move(fullSource, destPath, overwrite: false);
                return destPath;
            }
            catch (IOException)
            {
                // Fall back to safe copy+delete below (handles cross-mount junctions or transient locks)
            }
        }

        // Cross-volume safe move: copy to hidden temp name on target volume, rename atomically, then delete source
        var tempTransferPath = Path.Combine(fullDestDir, $".{Guid.NewGuid():N}.ytdl_transfer");
        try
        {
            await using (var src = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            await using (var dst = new FileStream(tempTransferPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await src.CopyToAsync(dst, ct);
                await dst.FlushAsync(ct);
            }

            File.Move(tempTransferPath, destPath, overwrite: false);
            try { File.Delete(fullSource); } catch { }
            return destPath;
        }
        catch
        {
            try { if (File.Exists(tempTransferPath)) File.Delete(tempTransferPath); } catch { }
            throw;
        }
    }

    private static string ResolveNonCollidingDestinationFile(string destinationDirectory, string preferredFileName)
    {
        var candidate = Path.Combine(destinationDirectory, preferredFileName);
        if (!File.Exists(candidate))
            return candidate;

        var nameNoExt = Path.GetFileNameWithoutExtension(preferredFileName);
        var ext = Path.GetExtension(preferredFileName);
        for (var i = 2; i < 1000; i++)
        {
            var alt = Path.Combine(destinationDirectory, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(alt))
                return alt;
        }

        return Path.Combine(destinationDirectory, $"{nameNoExt}_{Guid.NewGuid():N}{ext}");
    }

    private static string? TryGetNormalizedDriveRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    public static string SanitizeFileNameSegment(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "YouTube_Video";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);

        foreach (var ch in raw.Trim())
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "YouTube_Video";

        if (ReservedWindowsNames.Contains(cleaned))
            cleaned = "_" + cleaned;

        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength].TrimEnd(' ', '.');

        return string.IsNullOrWhiteSpace(cleaned) ? "YouTube_Video" : cleaned;
    }
}
