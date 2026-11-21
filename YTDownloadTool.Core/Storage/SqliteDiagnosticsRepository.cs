using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Logging;

namespace YTDownloadTool.Core.Storage;

public interface IDiagnosticsRepository
{
    Task RecordFailureAsync(DiagnosticEventRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<DiagnosticEventRecord>> GetRecentFailuresAsync(int limit = 25, CancellationToken ct = default);
    Task<int> GetFailureCountAsync(CancellationToken ct = default);
}

public sealed class SqliteDiagnosticsRepository : IDiagnosticsRepository
{
    public const int MaxRetainedEvents = 200;

    private readonly ISqliteDatabaseManager _db;
    private readonly ILogger<SqliteDiagnosticsRepository> _logger;

    public SqliteDiagnosticsRepository(ISqliteDatabaseManager db, ILogger<SqliteDiagnosticsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordFailureAsync(DiagnosticEventRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
        {
            _logger.LogWarning("Skipping DiagnosticEvents write because database schema is in recovery mode.");
            return;
        }

        var safeUserMsg = SensitiveDataRedactor.Redact(record.UserMessage);
        var safeTech = SensitiveDataRedactor.Redact(record.RedactedTechnicalDetails);
        if (safeTech.Length > 4000)
            safeTech = safeTech[..4000] + "... [truncated]";

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO DiagnosticEvents (
                    JobId, VideoId, Title, ErrorCategory, UserMessage,
                    RedactedTechnicalDetails, ExitCode, RetryAttempt, OccurredAtUtc
                ) VALUES (
                    $jobId, $videoId, $title, $errorCategory, $userMessage,
                    $redactedTechnicalDetails, $exitCode, $retryAttempt, $occurredAtUtc
                );
                """;
            insertCmd.Parameters.AddWithValue("$jobId", (object?)record.JobId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$videoId", (object?)record.VideoId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$title", SensitiveDataRedactor.Redact(record.Title));
            insertCmd.Parameters.AddWithValue("$errorCategory", record.ErrorCategory ?? "Unknown");
            insertCmd.Parameters.AddWithValue("$userMessage", safeUserMsg);
            insertCmd.Parameters.AddWithValue("$redactedTechnicalDetails", safeTech);
            insertCmd.Parameters.AddWithValue("$exitCode", record.ExitCode.HasValue ? record.ExitCode.Value : DBNull.Value);
            insertCmd.Parameters.AddWithValue("$retryAttempt", record.RetryAttempt);
            insertCmd.Parameters.AddWithValue("$occurredAtUtc", record.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var pruneCmd = conn.CreateCommand())
        {
            pruneCmd.Transaction = tx;
            pruneCmd.CommandText = """
                DELETE FROM DiagnosticEvents
                WHERE Id NOT IN (
                    SELECT Id FROM DiagnosticEvents
                    ORDER BY OccurredAtUtc DESC, Id DESC
                    LIMIT $maxRows
                );
                """;
            pruneCmd.Parameters.AddWithValue("$maxRows", MaxRetainedEvents);
            await pruneCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<DiagnosticEventRecord>> GetRecentFailuresAsync(int limit = 25, CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return [];

        var boundedLimit = Math.Clamp(limit, 1, MaxRetainedEvents);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, JobId, VideoId, Title, ErrorCategory, UserMessage,
                   RedactedTechnicalDetails, ExitCode, RetryAttempt, OccurredAtUtc
            FROM DiagnosticEvents
            ORDER BY OccurredAtUtc DESC, Id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", boundedLimit);

        var items = new List<DiagnosticEventRecord>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var occurredAt = DateTimeOffset.TryParse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            items.Add(new DiagnosticEventRecord
            {
                Id = reader.GetInt64(0),
                JobId = reader.IsDBNull(1) ? null : reader.GetString(1),
                VideoId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Title = reader.GetString(3),
                ErrorCategory = reader.GetString(4),
                UserMessage = reader.GetString(5),
                RedactedTechnicalDetails = reader.GetString(6),
                ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                RetryAttempt = reader.GetInt32(8),
                OccurredAtUtc = occurredAt
            });
        }

        return items;
    }

    public async Task<int> GetFailureCountAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        if (!_db.IsSchemaCompatible)
            return 0;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DiagnosticEvents;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
