namespace YTDownloadTool.Core.Settings;

public enum LivestreamRecordingMode
{
    /// <summary>Mode A: Record from the current live edge until the livestream ends.</summary>
    RecordFromNow = 0,

    /// <summary>Mode B: Start from the beginning (DVR) and record until the livestream ends.</summary>
    RecordFromStartUntilEnd = 1,

    /// <summary>Mode C: Start from the beginning (DVR) and stop near the current live edge.</summary>
    RecordFromStartToLiveEdge = 2
}

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = string.Empty;
    public string DefaultQuality { get; set; } = "1080p";
    public string DefaultContainer { get; set; } = "mp4";
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>Per-download speed limit in KB/s (0 = Unlimited), passed to yt-dlp as --limit-rate.</summary>
    public int PerDownloadSpeedLimitKbps { get; set; } = 0;

    public LivestreamRecordingMode DefaultLivestreamMode { get; set; } = LivestreamRecordingMode.RecordFromNow;
    public int LiveEdgeMarginSeconds { get; set; } = 30;

    /// <summary>"stable" or "nightly"</summary>
    public string YtDlpChannel { get; set; } = "stable";
    public bool AutoUpdateTools { get; set; } = true;

    public bool AutoCheckAppUpdates { get; set; } = true;
    public int AppUpdateCheckIntervalHours { get; set; } = 12;
    public DateTimeOffset? LastAppUpdateCheckUtc { get; set; }

    public AppSettings Clone() => new()
    {
        DownloadDirectory = DownloadDirectory,
        DefaultQuality = DefaultQuality,
        DefaultContainer = DefaultContainer,
        MaxConcurrentDownloads = MaxConcurrentDownloads,
        PerDownloadSpeedLimitKbps = PerDownloadSpeedLimitKbps,
        DefaultLivestreamMode = DefaultLivestreamMode,
        LiveEdgeMarginSeconds = LiveEdgeMarginSeconds,
        YtDlpChannel = YtDlpChannel,
        AutoUpdateTools = AutoUpdateTools,
        AutoCheckAppUpdates = AutoCheckAppUpdates,
        AppUpdateCheckIntervalHours = AppUpdateCheckIntervalHours,
        LastAppUpdateCheckUtc = LastAppUpdateCheckUtc
    };

    public void Normalize(string fallbackDownloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(DownloadDirectory))
            DownloadDirectory = fallbackDownloadDirectory;

        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 4);
        PerDownloadSpeedLimitKbps = Math.Clamp(PerDownloadSpeedLimitKbps, 0, 1_000_000);
        LiveEdgeMarginSeconds = Math.Clamp(LiveEdgeMarginSeconds, 15, 300);
        AppUpdateCheckIntervalHours = Math.Clamp(AppUpdateCheckIntervalHours, 12, 168);

        if (!string.Equals(YtDlpChannel, "nightly", StringComparison.OrdinalIgnoreCase))
            YtDlpChannel = "stable";
        else
            YtDlpChannel = "nightly";

        DefaultContainer = DefaultContainer?.ToLowerInvariant() switch
        {
            "mp4" or "mkv" or "webm" => DefaultContainer.ToLowerInvariant(),
            _ => "mp4"
        };
    }
}
