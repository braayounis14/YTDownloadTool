using System.Text.RegularExpressions;

namespace YTDownloadTool.Core.Tools;

public static partial class ToolVersionParser
{
    public static readonly Version MinimumDenoVersion = new(2, 3, 0);

    [GeneratedRegex(@"^(\d{4}\.\d{2}\.\d{2}(?:\.\d+)?)$", RegexOptions.Compiled)]
    private static partial Regex YtDlpVersionRegex();

    [GeneratedRegex(@"ffmpeg\s+version\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FFmpegVersionRegex();

    [GeneratedRegex(@"deno\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DenoVersionRegex();

    public static string? ParseYtDlpVersion(string stdOut)
    {
        var firstLine = stdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
            return null;

        var match = YtDlpVersionRegex().Match(firstLine);
        return match.Success ? match.Groups[1].Value : firstLine;
    }

    public static string? ParseFFmpegVersion(string stdOut)
    {
        var match = FFmpegVersionRegex().Match(stdOut);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? ParseDenoVersion(string stdOut)
    {
        var match = DenoVersionRegex().Match(stdOut);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool IsDenoVersionSupported(string? versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
            return false;

        var cleaned = versionString.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var parsed) && parsed >= MinimumDenoVersion;
    }

    public static string SanitizeVersionForFolder(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(version.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned[..Math.Min(cleaned.Length, 48)];
    }
}
