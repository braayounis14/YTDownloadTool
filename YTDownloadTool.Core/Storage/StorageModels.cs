using System.Text.Json;
using System.Text.Json.Serialization;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Storage;

public enum DatabaseHealthStatus
{
    Healthy = 0,
    Recovered = 1,
    Error = 2
}

public sealed record DatabaseHealthSnapshot(
    DatabaseHealthStatus Status,
    int CurrentSchemaVersion,
    int TargetSchemaVersion,
    bool IsSchemaCompatible,
    long DatabaseSizeBytes,
    int BackupCount,
    string DatabasePath,
    string BackupsDirectory,
    string? LastBackupPath = null,
    string? RecoveryNotice = null,
    int MigratedLegacyHistoryCount = 0);

public sealed record PersistedDownloadJob
{
    private static readonly JsonSerializerOptions QualityJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string JobId { get; init; } = string.Empty;
    public string VideoId { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string? ThumbnailUrl { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public string TempDirectory { get; init; } = string.Empty;
    public string? RelativeBaseFileName { get; init; }
    public string QualityOptionJson { get; init; } = string.Empty;
    public string OutputContainer { get; init; } = "mp4";
    public MediaLiveStatus LiveStatus { get; init; } = MediaLiveStatus.NotLive;
    public LivestreamRecordingMode? LivestreamMode { get; init; }
    public int LiveEdgeMarginSeconds { get; init; } = 30;
    public DateTimeOffset? LivestreamStartUtc { get; init; }
    public bool IsDvrAvailable { get; init; }
    public bool HasMuxedHlsFromStart { get; init; }
    public double? KnownDurationSeconds { get; init; }
    public long? EstimatedBytes { get; init; }
    public long QueueOrder { get; init; }
    public DownloadJobState Status { get; init; } = DownloadJobState.Queued;
    public double ProgressPercent { get; init; }
    public long? DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public int RetryAttempt { get; init; }
    public int MaxRetryAttempts { get; init; }
    public string? LastErrorCategory { get; init; }
    public string? LastErrorMessage { get; init; }
    public string? LastTechnicalDetails { get; init; }
    public string? OutputFilePath { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsActiveLivestream => LiveStatus == MediaLiveStatus.IsLive;

    public static string SerializeQualityOption(QualityOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return JsonSerializer.Serialize(option, QualityJsonOptions);
    }

    public QualityOption DeserializeQualityOption()
    {
        if (!string.IsNullOrWhiteSpace(QualityOptionJson))
        {
            var parsed = JsonSerializer.Deserialize<QualityOption>(QualityOptionJson, QualityJsonOptions);
            if (parsed is not null)
                return parsed;
        }

        return new QualityOption(
            Id: "best",
            Label: "Best Available",
            IsAudioOnly: false,
            Height: null,
            Fps: null,
            CodecSummary: null,
            EstimatedSizeBytes: EstimatedBytes,
            FormatSelector: "bv*+ba/b",
            PreferredAudioExtension: null,
            RequiresFFmpeg: true,
            SupportedContainers: ["mp4", "mkv", "webm"]);
    }

    public DownloadJobRequest ToDownloadJobRequest()
    {
        return new DownloadJobRequest(
            JobId: JobId,
            VideoId: VideoId,
            SourceUrl: SourceUrl,
            Title: Title,
            Channel: Channel,
            ThumbnailUrl: ThumbnailUrl,
            OutputDirectory: OutputDirectory,
            SelectedQuality: DeserializeQualityOption(),
            OutputContainer: OutputContainer,
            LiveStatus: LiveStatus,
            LivestreamMode: LivestreamMode,
            LiveEdgeMarginSeconds: LiveEdgeMarginSeconds,
            LivestreamStartUtc: LivestreamStartUtc,
            IsDvrAvailable: IsDvrAvailable,
            HasMuxedHlsFromStart: HasMuxedHlsFromStart,
            KnownDurationSeconds: KnownDurationSeconds);
    }

    public static PersistedDownloadJob FromRequest(
        DownloadJobRequest request,
        string tempDirectory,
        string? relativeBaseFileName = null,
        long queueOrder = 0)
    {
        var now = DateTimeOffset.UtcNow;
        return new PersistedDownloadJob
        {
            JobId = request.JobId,
            VideoId = request.VideoId,
            SourceUrl = request.SourceUrl,
            Title = request.Title,
            Channel = request.Channel,
            ThumbnailUrl = request.ThumbnailUrl,
            OutputDirectory = request.OutputDirectory,
            TempDirectory = tempDirectory,
            RelativeBaseFileName = relativeBaseFileName,
            QualityOptionJson = SerializeQualityOption(request.SelectedQuality),
            OutputContainer = request.OutputContainer,
            LiveStatus = request.LiveStatus,
            LivestreamMode = request.LivestreamMode,
            LiveEdgeMarginSeconds = request.LiveEdgeMarginSeconds,
            LivestreamStartUtc = request.LivestreamStartUtc,
            IsDvrAvailable = request.IsDvrAvailable,
            HasMuxedHlsFromStart = request.HasMuxedHlsFromStart,
            KnownDurationSeconds = request.KnownDurationSeconds,
            EstimatedBytes = request.SelectedQuality.EstimatedSizeBytes,
            QueueOrder = queueOrder,
            Status = DownloadJobState.Queued,
            TotalBytes = request.SelectedQuality.EstimatedSizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}

public sealed record DiagnosticEventRecord
{
    public long Id { get; init; }
    public string? JobId { get; init; }
    public string? VideoId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ErrorCategory { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public string RedactedTechnicalDetails { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
    public int RetryAttempt { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
