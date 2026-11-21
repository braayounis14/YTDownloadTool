using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Paths;

namespace YTDownloadTool.Core.Settings;

public enum MediaIntegrityStatus
{
    Unverified = 0,
    Verified = 1,
    Repaired = 2,
    NeedsAttention = 3
}

public sealed record DownloadHistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string VideoId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string OutputFilePath { get; init; } = string.Empty;
    public string? ThumbnailUrl { get; init; }
    public string QualityLabel { get; init; } = string.Empty;
    public string Container { get; init; } = string.Empty;
    public string? Resolution { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public long FileSizeBytes { get; init; }
    public double? DurationSeconds { get; init; }
    public bool WasLivestream { get; init; }
    public LivestreamRecordingMode? LivestreamMode { get; init; }
    public MediaIntegrityStatus IntegrityStatus { get; init; } = MediaIntegrityStatus.Unverified;
    public string? IntegrityMessage { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAtUtc { get; init; }
}

public interface IHistoryStore
{
    Task<IReadOnlyList<DownloadHistoryEntry>> LoadAsync(CancellationToken ct = default);
    Task AddAsync(DownloadHistoryEntry entry, CancellationToken ct = default);
    Task UpdateIntegrityAsync(
        string id,
        MediaIntegrityStatus status,
        string? integrityMessage,
        string? resolution,
        string? videoCodec,
        string? audioCodec,
        long? fileSizeBytes,
        double? durationSeconds,
        DateTimeOffset verifiedAtUtc,
        CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed class HistoryStore : IHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<HistoryStore> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public HistoryStore(IAppPaths paths, ILogger<HistoryStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DownloadHistoryEntry>> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            return await ReadInternalAsync(ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task AddAsync(DownloadHistoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _ioLock.WaitAsync(ct);
        try
        {
            var list = await ReadInternalAsync(ct);
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);

            await WriteAtomicAsync(list, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task UpdateIntegrityAsync(
        string id,
        MediaIntegrityStatus status,
        string? integrityMessage,
        string? resolution,
        string? videoCodec,
        string? audioCodec,
        long? fileSizeBytes,
        double? durationSeconds,
        DateTimeOffset verifiedAtUtc,
        CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var list = await ReadInternalAsync(ct);
            var index = list.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (index >= 0)
            {
                var existing = list[index];
                list[index] = existing with
                {
                    IntegrityStatus = status,
                    IntegrityMessage = integrityMessage,
                    Resolution = resolution ?? existing.Resolution,
                    VideoCodec = videoCodec ?? existing.VideoCodec,
                    AudioCodec = audioCodec ?? existing.AudioCodec,
                    FileSizeBytes = fileSizeBytes ?? existing.FileSizeBytes,
                    DurationSeconds = durationSeconds ?? existing.DurationSeconds,
                    VerifiedAtUtc = verifiedAtUtc
                };
                await WriteAtomicAsync(list, ct);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var list = await ReadInternalAsync(ct);
            if (list.RemoveAll(e => string.Equals(e.Id, id, StringComparison.Ordinal)) > 0)
            {
                await WriteAtomicAsync(list, ct);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            await WriteAtomicAsync([], ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<List<DownloadHistoryEntry>> ReadInternalAsync(CancellationToken ct)
    {
        _paths.EnsureDirectoriesExist();
        if (!File.Exists(_paths.HistoryFilePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(_paths.HistoryFilePath);
            return await JsonSerializer.DeserializeAsync<List<DownloadHistoryEntry>>(stream, JsonOptions, ct) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Download history file was unreadable; starting fresh.");
            return [];
        }
    }

    private async Task WriteAtomicAsync(List<DownloadHistoryEntry> entries, CancellationToken ct)
    {
        _paths.EnsureDirectoriesExist();
        var tempPath = $"{_paths.HistoryFilePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempPath, _paths.HistoryFilePath, overwrite: true);
    }
}
