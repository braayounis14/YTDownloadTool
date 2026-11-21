using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Storage;

public sealed record ProtectedTempDirectorySnapshot(
    bool CanSafelyPrune,
    IReadOnlySet<string> ProtectedFolderNames);

public interface IDownloadJobRepository
{
    Task<PersistedDownloadJob> InsertQueuedJobAsync(PersistedDownloadJob job, CancellationToken ct = default);
    Task<PersistedDownloadJob?> GetByIdAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<PersistedDownloadJob>> GetQueuedJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PersistedDownloadJob>> GetNonTerminalJobsAsync(CancellationToken ct = default);
    Task<PersistedDownloadJob?> DequeueNextQueuedJobAsync(CancellationToken ct = default);
    Task<bool> UpdateJobStateAsync(
        string jobId,
        DownloadJobState status,
        double? progressPercent = null,
        long? downloadedBytes = null,
        long? totalBytes = null,
        string? relativeBaseFileName = null,
        int? retryAttempt = null,
        int? maxRetryAttempts = null,
        string? lastErrorCategory = null,
        string? lastErrorMessage = null,
        string? lastTechnicalDetails = null,
        string? outputFilePath = null,
        bool clearError = false,
        CancellationToken ct = default);
    Task<bool> UpdateQualityAndEstimatedBytesAsync(
        string jobId,
        string qualityOptionJson,
        long? estimatedBytes,
        CancellationToken ct = default);
    Task<bool> MoveQueuedJobToTopAsync(string jobId, CancellationToken ct = default);
    Task<bool> MoveQueuedJobUpAsync(string jobId, CancellationToken ct = default);
    Task<bool> MoveQueuedJobDownAsync(string jobId, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default);
    Task<ProtectedTempDirectorySnapshot> GetProtectedTempDirectoryNamesAsync(CancellationToken ct = default);
}

public sealed class SqliteDownloadJobRepository : IDownloadJobRepository
{
    private const string SelectColumns = """
        JobId, VideoId, SourceUrl, Title, Channel, ThumbnailUrl,
        OutputDirectory, TempDirectory, RelativeBaseFileName, QualityOptionJson,
        OutputContainer, LiveStatus, LivestreamMode, LiveEdgeMarginSeconds,
        LivestreamStartUtc, IsDvrAvailable, HasMuxedHlsFromStart, KnownDurationSeconds,
        EstimatedBytes, QueueOrder, Status, ProgressPercent, DownloadedBytes, TotalBytes,
        RetryAttempt, MaxRetryAttempts, LastErrorCategory, LastErrorMessage,
        LastTechnicalDetails, OutputFilePath, CreatedAtUtc, UpdatedAtUtc
        """;

    private readonly ISqliteDatabaseManager _db;
    private readonly ILogger<SqliteDownloadJobRepository> _logger;

    public SqliteDownloadJobRepository(ISqliteDatabaseManager db, ILogger<SqliteDownloadJobRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PersistedDownloadJob> InsertQueuedJobAsync(PersistedDownloadJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            _logger.LogWarning("Database schema is in recovery mode; DownloadJob '{JobId}' will run without SQLite persistence.", job.JobId);
            return job;
        }

        var now = DateTimeOffset.UtcNow;
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var assignedOrder = job.QueueOrder;
        if (assignedOrder <= 0)
        {
            using var maxCmd = conn.CreateCommand();
            maxCmd.Transaction = tx;
            maxCmd.CommandText = "SELECT COALESCE(MAX(QueueOrder), 0) FROM DownloadJobs;";
            var currentMax = Convert.ToInt64(await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            assignedOrder = currentMax + 1;
        }

        var persisted = job with
        {
            QueueOrder = assignedOrder,
            CreatedAtUtc = job.CreatedAtUtc == default ? now : job.CreatedAtUtc,
            UpdatedAtUtc = now
        };

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO DownloadJobs (
                JobId, VideoId, SourceUrl, Title, Channel, ThumbnailUrl,
                OutputDirectory, TempDirectory, RelativeBaseFileName, QualityOptionJson,
                OutputContainer, LiveStatus, LivestreamMode, LiveEdgeMarginSeconds,
                LivestreamStartUtc, IsDvrAvailable, HasMuxedHlsFromStart, KnownDurationSeconds,
                EstimatedBytes, QueueOrder, Status, ProgressPercent, DownloadedBytes, TotalBytes,
                RetryAttempt, MaxRetryAttempts, LastErrorCategory, LastErrorMessage,
                LastTechnicalDetails, OutputFilePath, CreatedAtUtc, UpdatedAtUtc
            ) VALUES (
                $jobId, $videoId, $sourceUrl, $title, $channel, $thumbnailUrl,
                $outputDirectory, $tempDirectory, $relativeBaseFileName, $qualityOptionJson,
                $outputContainer, $liveStatus, $livestreamMode, $liveEdgeMarginSeconds,
                $livestreamStartUtc, $isDvrAvailable, $hasMuxedHlsFromStart, $knownDurationSeconds,
                $estimatedBytes, $queueOrder, $status, $progressPercent, $downloadedBytes, $totalBytes,
                $retryAttempt, $maxRetryAttempts, $lastErrorCategory, $lastErrorMessage,
                $lastTechnicalDetails, $outputFilePath, $createdAtUtc, $updatedAtUtc
            );
            """;

        cmd.Parameters.AddWithValue("$jobId", persisted.JobId);
        cmd.Parameters.AddWithValue("$videoId", persisted.VideoId ?? string.Empty);
        cmd.Parameters.AddWithValue("$sourceUrl", persisted.SourceUrl);
        cmd.Parameters.AddWithValue("$title", persisted.Title);
        cmd.Parameters.AddWithValue("$channel", persisted.Channel ?? string.Empty);
        cmd.Parameters.AddWithValue("$thumbnailUrl", (object?)persisted.ThumbnailUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outputDirectory", persisted.OutputDirectory);
        cmd.Parameters.AddWithValue("$tempDirectory", persisted.TempDirectory);
        cmd.Parameters.AddWithValue("$relativeBaseFileName", (object?)persisted.RelativeBaseFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qualityOptionJson", persisted.QualityOptionJson);
        cmd.Parameters.AddWithValue("$outputContainer", persisted.OutputContainer);
        cmd.Parameters.AddWithValue("$liveStatus", (int)persisted.LiveStatus);
        cmd.Parameters.AddWithValue("$livestreamMode", persisted.LivestreamMode.HasValue ? (int)persisted.LivestreamMode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$liveEdgeMarginSeconds", persisted.LiveEdgeMarginSeconds);
        cmd.Parameters.AddWithValue("$livestreamStartUtc", persisted.LivestreamStartUtc.HasValue
            ? persisted.LivestreamStartUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$isDvrAvailable", persisted.IsDvrAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("$hasMuxedHlsFromStart", persisted.HasMuxedHlsFromStart ? 1 : 0);
        cmd.Parameters.AddWithValue("$knownDurationSeconds", persisted.KnownDurationSeconds.HasValue ? persisted.KnownDurationSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$estimatedBytes", persisted.EstimatedBytes.HasValue ? persisted.EstimatedBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$queueOrder", persisted.QueueOrder);
        cmd.Parameters.AddWithValue("$status", persisted.Status.ToString());
        cmd.Parameters.AddWithValue("$progressPercent", persisted.ProgressPercent);
        cmd.Parameters.AddWithValue("$downloadedBytes", persisted.DownloadedBytes.HasValue ? persisted.DownloadedBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$totalBytes", persisted.TotalBytes.HasValue ? persisted.TotalBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$retryAttempt", persisted.RetryAttempt);
        cmd.Parameters.AddWithValue("$maxRetryAttempts", persisted.MaxRetryAttempts);
        cmd.Parameters.AddWithValue("$lastErrorCategory", (object?)persisted.LastErrorCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastErrorMessage", (object?)persisted.LastErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastTechnicalDetails", (object?)persisted.LastTechnicalDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outputFilePath", (object?)persisted.OutputFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAtUtc", persisted.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updatedAtUtc", persisted.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        tx.Commit();
        return persisted;
    }

    public async Task<PersistedDownloadJob?> GetByIdAsync(string jobId, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return null;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM DownloadJobs WHERE JobId = $jobId LIMIT 1;";
        cmd.Parameters.AddWithValue("$jobId", jobId);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    public async Task<IReadOnlyList<PersistedDownloadJob>> GetQueuedJobsAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return [];

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM DownloadJobs
            WHERE Status = 'Queued'
            ORDER BY QueueOrder ASC, CreatedAtUtc ASC;
            """;

        var list = new List<PersistedDownloadJob>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapJob(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<PersistedDownloadJob>> GetNonTerminalJobsAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return [];

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM DownloadJobs
            WHERE Status NOT IN ('Completed', 'Cancelled', 'Failed')
            ORDER BY QueueOrder ASC, CreatedAtUtc ASC;
            """;

        var list = new List<PersistedDownloadJob>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapJob(reader));
        }

        return list;
    }

    public async Task<PersistedDownloadJob?> DequeueNextQueuedJobAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return null;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        PersistedDownloadJob? next;
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM DownloadJobs
                WHERE Status = 'Queued'
                ORDER BY QueueOrder ASC, CreatedAtUtc ASC
                LIMIT 1;
                """;
            using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            next = await reader.ReadAsync(ct).ConfigureAwait(false) ? MapJob(reader) : null;
        }

        if (next is null)
        {
            tx.Commit();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE DownloadJobs
                SET Status = 'Preparing',
                    UpdatedAtUtc = $updatedAtUtc
                WHERE JobId = $jobId AND Status = 'Queued';
                """;
            updateCmd.Parameters.AddWithValue("$jobId", next.JobId);
            updateCmd.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
            var affected = await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected == 0)
            {
                tx.Commit();
                return null;
            }
        }

        tx.Commit();
        return next with { Status = DownloadJobState.Preparing, UpdatedAtUtc = now };
    }

    public async Task<bool> UpdateJobStateAsync(
        string jobId,
        DownloadJobState status,
        double? progressPercent = null,
        long? downloadedBytes = null,
        long? totalBytes = null,
        string? relativeBaseFileName = null,
        int? retryAttempt = null,
        int? maxRetryAttempts = null,
        string? lastErrorCategory = null,
        string? lastErrorMessage = null,
        string? lastTechnicalDetails = null,
        string? outputFilePath = null,
        bool clearError = false,
        CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return false;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE DownloadJobs
            SET Status = $status,
                ProgressPercent = COALESCE($progressPercent, ProgressPercent),
                DownloadedBytes = COALESCE($downloadedBytes, DownloadedBytes),
                TotalBytes = COALESCE($totalBytes, TotalBytes),
                RelativeBaseFileName = COALESCE($relativeBaseFileName, RelativeBaseFileName),
                RetryAttempt = COALESCE($retryAttempt, RetryAttempt),
                MaxRetryAttempts = COALESCE($maxRetryAttempts, MaxRetryAttempts),
                LastErrorCategory = CASE WHEN $clearError = 1 THEN NULL ELSE COALESCE($lastErrorCategory, LastErrorCategory) END,
                LastErrorMessage = CASE WHEN $clearError = 1 THEN NULL ELSE COALESCE($lastErrorMessage, LastErrorMessage) END,
                LastTechnicalDetails = CASE WHEN $clearError = 1 THEN NULL ELSE COALESCE($lastTechnicalDetails, LastTechnicalDetails) END,
                OutputFilePath = COALESCE($outputFilePath, OutputFilePath),
                UpdatedAtUtc = $updatedAtUtc
            WHERE JobId = $jobId;
            """;

        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$progressPercent", progressPercent.HasValue ? progressPercent.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$downloadedBytes", downloadedBytes.HasValue ? downloadedBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$totalBytes", totalBytes.HasValue ? totalBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$relativeBaseFileName", (object?)relativeBaseFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$retryAttempt", retryAttempt.HasValue ? retryAttempt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$maxRetryAttempts", maxRetryAttempts.HasValue ? maxRetryAttempts.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$clearError", clearError ? 1 : 0);
        cmd.Parameters.AddWithValue("$lastErrorCategory", (object?)lastErrorCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastTechnicalDetails", (object?)lastTechnicalDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outputFilePath", (object?)outputFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> UpdateQualityAndEstimatedBytesAsync(
        string jobId,
        string qualityOptionJson,
        long? estimatedBytes,
        CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return false;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE DownloadJobs
            SET QualityOptionJson = $qualityOptionJson,
                EstimatedBytes = $estimatedBytes,
                TotalBytes = COALESCE($estimatedBytes, TotalBytes),
                UpdatedAtUtc = $updatedAtUtc
            WHERE JobId = $jobId;
            """;
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$qualityOptionJson", qualityOptionJson);
        cmd.Parameters.AddWithValue("$estimatedBytes", estimatedBytes.HasValue ? estimatedBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> MoveQueuedJobToTopAsync(string jobId, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return false;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var queued = await ReadQueuedOrderListAsync(conn, tx, ct).ConfigureAwait(false);
        var idx = queued.FindIndex(x => string.Equals(x.JobId, jobId, StringComparison.Ordinal));
        if (idx <= 0)
        {
            tx.Commit();
            return false;
        }

        var minOrder = queued.Min(x => x.QueueOrder);
        var newOrder = minOrder - 1;
        await UpdateQueueOrderAsync(conn, tx, jobId, newOrder, ct).ConfigureAwait(false);

        tx.Commit();
        return true;
    }

    public Task<bool> MoveQueuedJobUpAsync(string jobId, CancellationToken ct = default)
        => SwapQueuedJobAsync(jobId, direction: -1, ct);

    public Task<bool> MoveQueuedJobDownAsync(string jobId, CancellationToken ct = default)
        => SwapQueuedJobAsync(jobId, direction: +1, ct);

    public async Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return false;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DownloadJobs WHERE JobId = $jobId;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<ProtectedTempDirectorySnapshot> GetProtectedTempDirectoryNamesAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            return new ProtectedTempDirectorySnapshot(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT JobId, TempDirectory
            FROM DownloadJobs
            WHERE Status NOT IN ('Completed', 'Cancelled', 'Failed');
            """;

        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var jobId = reader.GetString(0);
            var tempDir = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            if (!string.IsNullOrWhiteSpace(jobId))
            {
                protectedNames.Add(jobId);
                protectedNames.Add(OutputPathGuard.SanitizeFileNameSegment(jobId, 48));
            }

            if (!string.IsNullOrWhiteSpace(tempDir))
            {
                var trimmed = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folderName = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(folderName))
                    protectedNames.Add(folderName);
            }
        }

        return new ProtectedTempDirectorySnapshot(true, protectedNames);
    }

    private async Task<bool> SwapQueuedJobAsync(string jobId, int direction, CancellationToken ct)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return false;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var queued = await ReadQueuedOrderListAsync(conn, tx, ct).ConfigureAwait(false);
        var idx = queued.FindIndex(x => string.Equals(x.JobId, jobId, StringComparison.Ordinal));
        var targetIdx = idx + direction;
        if (idx < 0 || targetIdx < 0 || targetIdx >= queued.Count)
        {
            tx.Commit();
            return false;
        }

        var current = queued[idx];
        var neighbor = queued[targetIdx];

        if (current.QueueOrder == neighbor.QueueOrder)
        {
            // Normalize distinct sequential QueueOrder values if a tie ever occurred
            var baseOrder = queued.Min(x => x.QueueOrder);
            for (var i = 0; i < queued.Count; i++)
            {
                queued[i] = (queued[i].JobId, baseOrder + i);
                await UpdateQueueOrderAsync(conn, tx, queued[i].JobId, queued[i].QueueOrder, ct).ConfigureAwait(false);
            }

            current = queued[idx];
            neighbor = queued[targetIdx];
        }

        await UpdateQueueOrderAsync(conn, tx, current.JobId, neighbor.QueueOrder, ct).ConfigureAwait(false);
        await UpdateQueueOrderAsync(conn, tx, neighbor.JobId, current.QueueOrder, ct).ConfigureAwait(false);

        tx.Commit();
        return true;
    }

    private static async Task<List<(string JobId, long QueueOrder)>> ReadQueuedOrderListAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT JobId, QueueOrder
            FROM DownloadJobs
            WHERE Status = 'Queued'
            ORDER BY QueueOrder ASC, CreatedAtUtc ASC;
            """;

        var list = new List<(string, long)>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return list;
    }

    private static async Task UpdateQueueOrderAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string jobId,
        long queueOrder,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE DownloadJobs
            SET QueueOrder = $queueOrder,
                UpdatedAtUtc = $updatedAtUtc
            WHERE JobId = $jobId;
            """;
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$queueOrder", queueOrder);
        cmd.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static PersistedDownloadJob MapJob(SqliteDataReader reader)
    {
        var statusRaw = reader.GetString(20);
        var status = Enum.TryParse<DownloadJobState>(statusRaw, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : DownloadJobState.Queued;

        DateTimeOffset? streamStart = null;
        if (!reader.IsDBNull(14)
            && DateTimeOffset.TryParse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedStart))
        {
            streamStart = parsedStart;
        }

        var createdAt = DateTimeOffset.TryParse(reader.GetString(30), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCreated)
            ? parsedCreated
            : DateTimeOffset.UtcNow;

        var updatedAt = DateTimeOffset.TryParse(reader.GetString(31), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedUpdated)
            ? parsedUpdated
            : createdAt;

        return new PersistedDownloadJob
        {
            JobId = reader.GetString(0),
            VideoId = reader.GetString(1),
            SourceUrl = reader.GetString(2),
            Title = reader.GetString(3),
            Channel = reader.GetString(4),
            ThumbnailUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
            OutputDirectory = reader.GetString(6),
            TempDirectory = reader.GetString(7),
            RelativeBaseFileName = reader.IsDBNull(8) ? null : reader.GetString(8),
            QualityOptionJson = reader.GetString(9),
            OutputContainer = reader.GetString(10),
            LiveStatus = (MediaLiveStatus)reader.GetInt32(11),
            LivestreamMode = reader.IsDBNull(12) ? null : (LivestreamRecordingMode)reader.GetInt32(12),
            LiveEdgeMarginSeconds = reader.GetInt32(13),
            LivestreamStartUtc = streamStart,
            IsDvrAvailable = reader.GetInt64(15) != 0,
            HasMuxedHlsFromStart = reader.GetInt64(16) != 0,
            KnownDurationSeconds = reader.IsDBNull(17) ? null : reader.GetDouble(17),
            EstimatedBytes = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            QueueOrder = reader.GetInt64(19),
            Status = status,
            ProgressPercent = reader.GetDouble(21),
            DownloadedBytes = reader.IsDBNull(22) ? null : reader.GetInt64(22),
            TotalBytes = reader.IsDBNull(23) ? null : reader.GetInt64(23),
            RetryAttempt = reader.GetInt32(24),
            MaxRetryAttempts = reader.GetInt32(25),
            LastErrorCategory = reader.IsDBNull(26) ? null : reader.GetString(26),
            LastErrorMessage = reader.IsDBNull(27) ? null : reader.GetString(27),
            LastTechnicalDetails = reader.IsDBNull(28) ? null : reader.GetString(28),
            OutputFilePath = reader.IsDBNull(29) ? null : reader.GetString(29),
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
    }
}
