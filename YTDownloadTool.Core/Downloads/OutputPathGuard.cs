using System.Text;

namespace YTDownloadTool.Core.Downloads;

public sealed class OutputPathReservation : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal OutputPathReservation(string reservedBasePath, string targetFilePath, Action release)
    {
        ReservedBasePath = reservedBasePath;
        TargetFilePath = targetFilePath;
        _release = release;
    }

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
        Directory.CreateDirectory(outputDirectory);

        var safeExt = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeExt))
            safeExt = "mp4";

        var sanitizedTitle = SanitizeFileNameSegment(title, maxLength: 100);
        var idPart = string.IsNullOrWhiteSpace(videoId) ? string.Empty : $" [{SanitizeFileNameSegment(videoId, 16)}]";
        var tagPart = string.IsNullOrWhiteSpace(suffixTag) ? string.Empty : $" ({SanitizeFileNameSegment(suffixTag, 24)})";
        var baseName = $"{sanitizedTitle}{tagPart}{idPart}";

        // Ensure total full path stays comfortably inside Windows MAX_PATH (260) even with .f137.mp4.part temp suffixes
        var maxBaseLen = Math.Max(20, 210 - outputDirectory.Length - safeExt.Length);
        if (baseName.Length > maxBaseLen)
            baseName = baseName[..maxBaseLen].TrimEnd(' ', '.');

        lock (_lock)
        {
            for (var counter = 1; counter < 1000; counter++)
            {
                var candidateBaseName = counter == 1 ? baseName : $"{baseName} ({counter})";
                var candidateBasePath = Path.GetFullPath(Path.Combine(outputDirectory, candidateBaseName));
                var candidateFinalPath = $"{candidateBasePath}.{safeExt}";

                if (_activeBasePaths.Contains(candidateBasePath))
                    continue;

                if (File.Exists(candidateFinalPath)
                    || File.Exists($"{candidateBasePath}.mkv")
                    || File.Exists($"{candidateBasePath}.ts")
                    || File.Exists($"{candidateFinalPath}.part"))
                {
                    continue;
                }

                _activeBasePaths.Add(candidateBasePath);
                return new OutputPathReservation(candidateBasePath, candidateFinalPath, () =>
                {
                    lock (_lock)
                    {
                        _activeBasePaths.Remove(candidateBasePath);
                    }
                });
            }
        }

        throw new IOException($"Could not reserve a unique filename in '{outputDirectory}' after 999 attempts.");
    }

    public static void EnsureSufficientDiskSpace(string directoryPath, long? estimatedSizeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
            if (string.IsNullOrWhiteSpace(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;

            // Require at least 200 MB minimum free space, or 2.2x estimated media size (for video+audio+FFmpeg merge headroom)
            const long minFreeBytes = 200L * 1024 * 1024;
            var requiredBytes = estimatedSizeBytes is > 0
                ? Math.Max(minFreeBytes, (long)(estimatedSizeBytes.Value * 2.2))
                : minFreeBytes;

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                var availableMb = drive.AvailableFreeSpace / (1024.0 * 1024.0);
                var requiredMb = requiredBytes / (1024.0 * 1024.0);
                throw new IOException(
                    $"Insufficient disk space on drive {drive.Name}. Available: {availableMb:F0} MB, recommended: {requiredMb:F0} MB.");
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
