using System.IO.Compression;

namespace YTDownloadTool.Core.Tools;

public static class SafeZipExtractor
{
    /// <summary>
    /// Safely extracts entries matching <paramref name="entryDestinationMapper"/> from a ZIP archive
    /// into <paramref name="destinationDirectory"/> with strict Zip-Slip path traversal prevention.
    /// If <paramref name="entryDestinationMapper"/> returns null for an entry, that entry is skipped.
    /// </summary>
    public static void ExtractFiltered(
        string zipFilePath,
        string destinationDirectory,
        Func<ZipArchiveEntry, string?> entryDestinationMapper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(entryDestinationMapper);

        Directory.CreateDirectory(destinationDirectory);
        var fullDestRoot = Path.GetFullPath(destinationDirectory);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
            fullDestRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            // Skip directory placeholders
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // Reject raw entry paths attempting traversal even before mapping
            if (entry.FullName.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' contains an unsafe path.");
            }

            var relativeTarget = entryDestinationMapper(entry);
            if (string.IsNullOrWhiteSpace(relativeTarget))
                continue;

            var combinedPath = Path.GetFullPath(Path.Combine(fullDestRoot, relativeTarget));
            if (!combinedPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes target directory '{destinationDirectory}'.");
            }

            var targetDir = Path.GetDirectoryName(combinedPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            entry.ExtractToFile(combinedPath, overwrite: true);
        }
    }

    /// <summary>
    /// Maps entries from yt-dlp/FFmpeg-Builds win64-gpl-shared.zip, keeping only runtime files
    /// (ffmpeg.exe, ffprobe.exe, shared DLLs, and LICENSE.txt) and discarding ffplay/doc/include/lib.
    /// </summary>
    public static string? MapFFmpegSharedEntry(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        var fileName = entry.Name;

        if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
        {
            if (fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return null;
        }

        if (fileName.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("COPYING.GPLv3", StringComparison.OrdinalIgnoreCase))
        {
            return "LICENSE.txt";
        }

        return null;
    }

    /// <summary>Maps entries from denoland/deno Windows zip, keeping deno.exe.</summary>
    public static string? MapDenoEntry(ZipArchiveEntry entry)
    {
        return entry.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase) ? "deno.exe" : null;
    }
}
