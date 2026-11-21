using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Storage;

public sealed class SqliteHistoryStore : IHistoryStore
{
    private const int MaxLoadedEntries = 1000;

    private readonly ISqliteDatabaseManager _db;
    private readonly ILogger<SqliteHistoryStore> _logger;

    public SqliteHistoryStore(ISqliteDatabaseManager db, ILogger<SqliteHistoryStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DownloadHistoryEntry>> LoadAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            _logger.LogWarning("Skipping LibraryItems load because database schema is in recovery mode.");
            return [];
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, VideoId, Title, Channel, SourceUrl, OutputFilePath, ThumbnailUrl,
                   QualityLabel, Container, Resolution, VideoCodec, AudioCodec,
                   FileSizeBytes, DurationSeconds, WasLivestream, LivestreamMode,
                   IntegrityStatus, IntegrityMessage, CompletedAtUtc, VerifiedAtUtc
            FROM LibraryItems
            ORDER BY CompletedAtUtc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", MaxLoadedEntries);

        var items = new List<DownloadHistoryEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(MapEntry(reader));
        }

        return items;
    }

    public async Task AddAsync(DownloadHistoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            _logger.LogWarning("Skipping LibraryItems write for '{Title}' because database schema is in recovery mode.", entry.Title);
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO LibraryItems (
                Id, VideoId, Title, Channel, SourceUrl, OutputFilePath, ThumbnailUrl,
                QualityLabel, Container, Resolution, VideoCodec, AudioCodec,
                FileSizeBytes, DurationSeconds, WasLivestream, LivestreamMode,
                IntegrityStatus, IntegrityMessage, CompletedAtUtc, VerifiedAtUtc
            ) VALUES (
                $id, $videoId, $title, $channel, $sourceUrl, $outputFilePath, $thumbnailUrl,
                $qualityLabel, $container, $resolution, $videoCodec, $audioCodec,
                $fileSizeBytes, $durationSeconds, $wasLivestream, $livestreamMode,
                $integrityStatus, $integrityMessage, $completedAtUtc, $verifiedAtUtc
            );
            """;

        cmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id);
        cmd.Parameters.AddWithValue("$videoId", entry.VideoId ?? string.Empty);
        cmd.Parameters.AddWithValue("$title", entry.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("$channel", entry.Channel ?? string.Empty);
        cmd.Parameters.AddWithValue("$sourceUrl", entry.SourceUrl ?? string.Empty);
        cmd.Parameters.AddWithValue("$outputFilePath", entry.OutputFilePath ?? string.Empty);
        cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)entry.ThumbnailUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qualityLabel", entry.QualityLabel ?? string.Empty);
        cmd.Parameters.AddWithValue("$container", entry.Container ?? string.Empty);
        cmd.Parameters.AddWithValue("$resolution", (object?)entry.Resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$videoCodec", (object?)entry.VideoCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$audioCodec", (object?)entry.AudioCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fileSizeBytes", entry.FileSizeBytes);
        cmd.Parameters.AddWithValue("$durationSeconds", entry.DurationSeconds.HasValue ? entry.DurationSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$wasLivestream", entry.WasLivestream ? 1 : 0);
        cmd.Parameters.AddWithValue("$livestreamMode", entry.LivestreamMode.HasValue ? (int)entry.LivestreamMode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$integrityStatus", entry.IntegrityStatus.ToString());
        cmd.Parameters.AddWithValue("$integrityMessage", (object?)entry.IntegrityMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completedAtUtc", entry.CompletedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$verifiedAtUtc", entry.VerifiedAtUtc.HasValue
            ? entry.VerifiedAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            _logger.LogWarning("Skipping LibraryItems integrity update because database schema is in recovery mode.");
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE LibraryItems
            SET IntegrityStatus = $integrityStatus,
                IntegrityMessage = $integrityMessage,
                Resolution = COALESCE($resolution, Resolution),
                VideoCodec = COALESCE($videoCodec, VideoCodec),
                AudioCodec = COALESCE($audioCodec, AudioCodec),
                FileSizeBytes = COALESCE($fileSizeBytes, FileSizeBytes),
                DurationSeconds = COALESCE($durationSeconds, DurationSeconds),
                VerifiedAtUtc = $verifiedAtUtc
            WHERE Id = $id;
            """;

        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$integrityStatus", status.ToString());
        cmd.Parameters.AddWithValue("$integrityMessage", (object?)integrityMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resolution", (object?)resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$videoCodec", (object?)videoCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$audioCodec", (object?)audioCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fileSizeBytes", fileSizeBytes.HasValue ? fileSizeBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$durationSeconds", durationSeconds.HasValue ? durationSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$verifiedAtUtc", verifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LibraryItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LibraryItems;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DownloadHistoryEntry MapEntry(SqliteDataReader reader)
    {
        var integrityRaw = reader.IsDBNull(16) ? null : reader.GetString(16);
        var integrity = Enum.TryParse<MediaIntegrityStatus>(integrityRaw, ignoreCase: true, out var parsedIntegrity)
            ? parsedIntegrity
            : MediaIntegrityStatus.Unverified;

        var completedAtRaw = reader.GetString(18);
        var completedAt = DateTimeOffset.TryParse(completedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCompleted)
            ? parsedCompleted
            : DateTimeOffset.UtcNow;

        DateTimeOffset? verifiedAt = null;
        if (!reader.IsDBNull(19)
            && DateTimeOffset.TryParse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedVerified))
        {
            verifiedAt = parsedVerified;
        }

        return new DownloadHistoryEntry
        {
            Id = reader.GetString(0),
            VideoId = reader.GetString(1),
            Title = reader.GetString(2),
            Channel = reader.GetString(3),
            SourceUrl = reader.GetString(4),
            OutputFilePath = reader.GetString(5),
            ThumbnailUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            QualityLabel = reader.GetString(7),
            Container = reader.GetString(8),
            Resolution = reader.IsDBNull(9) ? null : reader.GetString(9),
            VideoCodec = reader.IsDBNull(10) ? null : reader.GetString(10),
            AudioCodec = reader.IsDBNull(11) ? null : reader.GetString(11),
            FileSizeBytes = reader.GetInt64(12),
            DurationSeconds = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            WasLivestream = reader.GetInt64(14) != 0,
            LivestreamMode = reader.IsDBNull(15) ? null : (LivestreamRecordingMode)reader.GetInt32(15),
            IntegrityStatus = integrity,
            IntegrityMessage = reader.IsDBNull(17) ? null : reader.GetString(17),
            CompletedAtUtc = completedAt,
            VerifiedAtUtc = verifiedAt
        };
    }
}
