using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Downloads;

public enum MediaLiveStatus
{
    NotLive = 0,
    IsLive = 1,
    IsUpcoming = 2,
    WasLive = 3,
    PostLive = 4,
    Unknown = 5
}

public sealed record RawMediaFormat(
    string FormatId,
    string Ext,
    string Protocol,
    int? Width,
    int? Height,
    double? Fps,
    string? VideoCodec,
    string? AudioCodec,
    double? TbrKbps,
    long? FileSizeBytes,
    long? FileSizeApproxBytes,
    string? FormatNote,
    string? DynamicRange)
{
    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoCodec)
                            && !VideoCodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                            && Height is > 0;

    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec)
                            && !AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);

    public bool IsMuxed => HasVideo && HasAudio;

    public bool IsHls => Protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase);

    public bool IsDash => Protocol.Contains("dash", StringComparison.OrdinalIgnoreCase);

    public long? BestKnownSizeBytes => FileSizeBytes ?? FileSizeApproxBytes;
}

public sealed record QualityOption(
    string Id,
    string Label,
    bool IsAudioOnly,
    int? Height,
    int? Fps,
    string? CodecSummary,
    long? EstimatedSizeBytes,
    string FormatSelector,
    string? PreferredAudioExtension,
    bool RequiresFFmpeg,
    IReadOnlyList<string> SupportedContainers,
    bool IsMuxedHls = false)
{
    public string ResolutionBadge => IsAudioOnly
        ? "AUDIO"
        : Height switch
        {
            >= 4320 => "8K",
            >= 2160 => "4K",
            >= 1440 => "2K",
            >= 1080 => "1080p",
            >= 720 => "720p",
            > 0 => $"{Height}p",
            _ => "BEST"
        };

    public override string ToString() => Label;
}

public sealed record DvrProbeInfo(
    bool IsDvrAvailable,
    bool IsDvrWindowCapped,
    TimeSpan? StreamElapsed,
    string? WarningMessage,
    bool HasMuxedHlsFromStart,
    IReadOnlyList<QualityOption> FromStartQualities);

public sealed record MediaProbeResult(
    string VideoId,
    string CanonicalUrl,
    string Title,
    string Channel,
    double? DurationSeconds,
    string? ThumbnailUrl,
    MediaLiveStatus LiveStatus,
    DateTimeOffset? ReleaseTimestampUtc,
    IReadOnlyList<QualityOption> AvailableQualities,
    IReadOnlyList<RawMediaFormat> RawFormats,
    DvrProbeInfo? DvrInfo,
    IReadOnlyList<string> Warnings)
{
    public bool IsActiveLivestream => LiveStatus == MediaLiveStatus.IsLive;
    public bool IsCompletedLivestream => LiveStatus == MediaLiveStatus.WasLive;
    public bool IsUpcomingLivestream => LiveStatus == MediaLiveStatus.IsUpcoming;
    public bool IsPostLiveProcessing => LiveStatus == MediaLiveStatus.PostLive;

    public string ContentTypeBadge => LiveStatus switch
    {
        MediaLiveStatus.IsLive => "LIVE",
        MediaLiveStatus.IsUpcoming => "UPCOMING LIVE",
        MediaLiveStatus.WasLive => "COMPLETED LIVESTREAM",
        MediaLiveStatus.PostLive => "LIVE PROCESSING",
        _ => "VIDEO"
    };
}

public enum DownloadJobState
{
    Queued = 0,
    Preparing = 1,
    Downloading = 2,
    CatchingUp = 3,
    RecordingLive = 4,
    Finalizing = 5,
    Completed = 6,
    Cancelled = 7,
    Failed = 8,
    Paused = 9,
    Retrying = 10,
    InterruptedLivestream = 11
}

public sealed record DownloadJobRequest(
    string JobId,
    string VideoId,
    string SourceUrl,
    string Title,
    string Channel,
    string? ThumbnailUrl,
    string OutputDirectory,
    QualityOption SelectedQuality,
    string OutputContainer,
    MediaLiveStatus LiveStatus,
    LivestreamRecordingMode? LivestreamMode = null,
    int LiveEdgeMarginSeconds = 30,
    DateTimeOffset? LivestreamStartUtc = null,
    bool IsDvrAvailable = false,
    bool HasMuxedHlsFromStart = false,
    double? KnownDurationSeconds = null);

public sealed record DownloadProgressSnapshot(
    string JobId,
    DownloadJobState State,
    double Percentage,
    bool IsIndeterminate,
    long? DownloadedBytes,
    long? TotalBytes,
    double? SpeedBytesPerSecond,
    TimeSpan? Eta,
    TimeSpan? RecordedDuration,
    string PhaseLabel,
    string? OutputFilePath = null,
    string? ErrorMessage = null,
    string? TechnicalDetails = null,
    string? NonFatalNotice = null);
