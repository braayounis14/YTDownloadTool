using Microsoft.Data.Sqlite;

namespace YTDownloadTool.Core.Storage;

public sealed class InitialSchemaMigrationV1 : ISchemaMigration
{
    public int TargetVersion => 1;

    public string Description => "Create DownloadJobs, LibraryItems, and DiagnosticEvents tables with indexes.";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DownloadJobs (
                JobId                   TEXT    PRIMARY KEY NOT NULL,
                VideoId                 TEXT    NOT NULL DEFAULT '',
                SourceUrl               TEXT    NOT NULL,
                Title                   TEXT    NOT NULL,
                Channel                 TEXT    NOT NULL DEFAULT '',
                ThumbnailUrl            TEXT    NULL,
                OutputDirectory         TEXT    NOT NULL,
                TempDirectory           TEXT    NOT NULL,
                RelativeBaseFileName    TEXT    NULL,
                QualityOptionJson       TEXT    NOT NULL,
                OutputContainer         TEXT    NOT NULL DEFAULT 'mp4',
                LiveStatus              INTEGER NOT NULL DEFAULT 0,
                LivestreamMode          INTEGER NULL,
                LiveEdgeMarginSeconds   INTEGER NOT NULL DEFAULT 30,
                LivestreamStartUtc      TEXT    NULL,
                IsDvrAvailable          INTEGER NOT NULL DEFAULT 0,
                HasMuxedHlsFromStart    INTEGER NOT NULL DEFAULT 0,
                KnownDurationSeconds    REAL    NULL,
                EstimatedBytes          INTEGER NULL,
                QueueOrder              INTEGER NOT NULL DEFAULT 0,
                Status                  TEXT    NOT NULL DEFAULT 'Queued',
                ProgressPercent         REAL    NOT NULL DEFAULT 0,
                DownloadedBytes         INTEGER NULL,
                TotalBytes              INTEGER NULL,
                RetryAttempt            INTEGER NOT NULL DEFAULT 0,
                MaxRetryAttempts        INTEGER NOT NULL DEFAULT 0,
                LastErrorCategory       TEXT    NULL,
                LastErrorMessage        TEXT    NULL,
                LastTechnicalDetails    TEXT    NULL,
                OutputFilePath          TEXT    NULL,
                CreatedAtUtc            TEXT    NOT NULL,
                UpdatedAtUtc            TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_DownloadJobs_Queue
                ON DownloadJobs (Status, QueueOrder ASC, CreatedAtUtc ASC);

            CREATE TABLE IF NOT EXISTS LibraryItems (
                Id                  TEXT    PRIMARY KEY NOT NULL,
                VideoId             TEXT    NOT NULL DEFAULT '',
                Title               TEXT    NOT NULL,
                Channel             TEXT    NOT NULL DEFAULT '',
                SourceUrl           TEXT    NOT NULL DEFAULT '',
                OutputFilePath      TEXT    NOT NULL,
                ThumbnailUrl        TEXT    NULL,
                QualityLabel        TEXT    NOT NULL DEFAULT '',
                Container           TEXT    NOT NULL DEFAULT '',
                Resolution          TEXT    NULL,
                VideoCodec          TEXT    NULL,
                AudioCodec          TEXT    NULL,
                FileSizeBytes       INTEGER NOT NULL DEFAULT 0,
                DurationSeconds     REAL    NULL,
                WasLivestream       INTEGER NOT NULL DEFAULT 0,
                LivestreamMode      INTEGER NULL,
                IntegrityStatus     TEXT    NOT NULL DEFAULT 'Unverified',
                IntegrityMessage    TEXT    NULL,
                CompletedAtUtc      TEXT    NOT NULL,
                VerifiedAtUtc       TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_LibraryItems_CompletedAt
                ON LibraryItems (CompletedAtUtc DESC);

            CREATE TABLE IF NOT EXISTS DiagnosticEvents (
                Id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId                       TEXT    NULL,
                VideoId                     TEXT    NULL,
                Title                       TEXT    NOT NULL DEFAULT '',
                ErrorCategory               TEXT    NOT NULL,
                UserMessage                 TEXT    NOT NULL,
                RedactedTechnicalDetails    TEXT    NOT NULL DEFAULT '',
                ExitCode                    INTEGER NULL,
                RetryAttempt                INTEGER NOT NULL DEFAULT 0,
                OccurredAtUtc               TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_DiagnosticEvents_OccurredAt
                ON DiagnosticEvents (OccurredAtUtc DESC, Id DESC);
            """;
        cmd.ExecuteNonQuery();
    }
}
