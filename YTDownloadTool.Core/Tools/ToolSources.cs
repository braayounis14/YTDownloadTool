namespace YTDownloadTool.Core.Tools;

public sealed record ToolSourceDefinition(
    ToolKind Kind,
    string DisplayName,
    string Owner,
    string Repository,
    string AssetName,
    string ChecksumAssetName,
    bool IsZipArchive,
    bool UsesRollingLatestTag,
    TimeSpan AutoCheckCadence);

public static class ToolSources
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    public static bool IsTrustedDownloadUri(Uri? uri)
    {
        if (uri is null)
            return false;

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && AllowedHosts.Contains(uri.Host);
    }

    public static ToolSourceDefinition GetDefinition(ToolKind kind, string ytDlpChannel = "stable")
    {
        return kind switch
        {
            ToolKind.YtDlp => string.Equals(ytDlpChannel, "nightly", StringComparison.OrdinalIgnoreCase)
                ? new ToolSourceDefinition(
                    Kind: ToolKind.YtDlp,
                    DisplayName: "yt-dlp (Nightly)",
                    Owner: "yt-dlp",
                    Repository: "yt-dlp-nightly-builds",
                    AssetName: "yt-dlp.exe",
                    ChecksumAssetName: "SHA2-256SUMS",
                    IsZipArchive: false,
                    UsesRollingLatestTag: false,
                    AutoCheckCadence: TimeSpan.FromHours(24))
                : new ToolSourceDefinition(
                    Kind: ToolKind.YtDlp,
                    DisplayName: "yt-dlp",
                    Owner: "yt-dlp",
                    Repository: "yt-dlp",
                    AssetName: "yt-dlp.exe",
                    ChecksumAssetName: "SHA2-256SUMS",
                    IsZipArchive: false,
                    UsesRollingLatestTag: false,
                    AutoCheckCadence: TimeSpan.FromHours(24)),

            ToolKind.FFmpeg => new ToolSourceDefinition(
                Kind: ToolKind.FFmpeg,
                DisplayName: "FFmpeg",
                Owner: "yt-dlp",
                Repository: "FFmpeg-Builds",
                AssetName: "ffmpeg-master-latest-win64-gpl-shared.zip",
                ChecksumAssetName: "checksums.sha256",
                IsZipArchive: true,
                UsesRollingLatestTag: true,
                AutoCheckCadence: TimeSpan.FromDays(30)),

            ToolKind.Deno => new ToolSourceDefinition(
                Kind: ToolKind.Deno,
                DisplayName: "Deno JS Runtime",
                Owner: "denoland",
                Repository: "deno",
                AssetName: "deno-x86_64-pc-windows-msvc.zip",
                ChecksumAssetName: "deno-x86_64-pc-windows-msvc.zip.sha256sum",
                IsZipArchive: true,
                UsesRollingLatestTag: false,
                AutoCheckCadence: TimeSpan.FromDays(30)),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported tool kind.")
        };
    }

    public static string BuildLatestReleasePageUrl(ToolSourceDefinition def) =>
        $"https://github.com/{def.Owner}/{def.Repository}/releases/latest";

    public static string BuildTagAssetDownloadUrl(ToolSourceDefinition def, string tag, string assetName) =>
        $"https://github.com/{def.Owner}/{def.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    public static string BuildLatestAssetDownloadUrl(ToolSourceDefinition def, string assetName) =>
        $"https://github.com/{def.Owner}/{def.Repository}/releases/latest/download/{Uri.EscapeDataString(assetName)}";
}
