namespace YTDownloadTool.Core.Tools;

public enum ToolKind
{
    YtDlp = 0,
    FFmpeg = 1,
    Deno = 2
}

public enum ToolHealthState
{
    Ready = 0,
    Missing = 1,
    Corrupted = 2,
    Outdated = 3,
    Updating = 4
}

public sealed class ToolManifestEntry
{
    public string ActiveVersion { get; set; } = string.Empty;
    public string? PreviousVersion { get; set; }
    public string Channel { get; set; } = "stable";
    public string Sha256 { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public DateTimeOffset InstalledAtUtc { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
}

public sealed class ToolManifest
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<ToolKind, ToolManifestEntry> Tools { get; set; } = new();
}

public sealed record ToolStatusInfo(
    ToolKind Kind,
    string DisplayName,
    ToolHealthState State,
    string? InstalledVersion,
    string? PreviousVersion,
    string? AvailableVersion,
    bool UpdateAvailable,
    bool CanRollback,
    string? ExecutablePath,
    DateTimeOffset? LastCheckedUtc,
    string Message);

public sealed record ResolvedToolchain(
    string YtDlpPath,
    string FFmpegDirectory,
    string FFmpegPath,
    string FFprobePath,
    string DenoPath,
    string YtDlpVersion,
    string FFmpegVersion,
    string DenoVersion);

public sealed record ToolUpdateCheckResult(
    ToolKind Kind,
    bool UpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? LatestAssetSha256,
    string? DownloadUrl,
    string? ChecksumUrl,
    string AssetName,
    string Message);
