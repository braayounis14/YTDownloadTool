using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;

namespace YTDownloadTool.Tests.Storage;

public sealed class SqliteStorageTests : IDisposable
{
    private readonly string _rootDir;
    private readonly DefaultAppPaths _paths;

    public SqliteStorageTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "YTDownloadTool_SqliteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _paths = new DefaultAppPaths(
            appDirectory: Path.Combine(_rootDir, "app"),
            localAppData: Path.Combine(_rootDir, "local"),
            roamingAppData: Path.Combine(_rootDir, "roaming"),
            videosFolder: Path.Combine(_rootDir, "videos"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch
        {
            // Ignore transient test cleanup lock
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesWalDatabaseWithSchemaVersion1AndForeignKeys()
    {
        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);

        var health = await db.InitializeAsync();

        Assert.Equal(DatabaseHealthStatus.Healthy, health.Status);
        Assert.True(health.IsSchemaCompatible);
        Assert.Equal(1, health.CurrentSchemaVersion);
        Assert.Equal(1, health.TargetSchemaVersion);
        Assert.True(File.Exists(_paths.DatabaseFilePath));

        using var conn = db.OpenConnection();
        Assert.Equal("wal", ExecuteScalarString(conn, "PRAGMA journal_mode;")?.ToLowerInvariant());
        Assert.Equal(1L, ExecuteScalarLong(conn, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, ExecuteScalarLong(conn, "PRAGMA user_version;"));

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        Assert.Contains("DownloadJobs", tables);
        Assert.Contains("LibraryItems", tables);
        Assert.Contains("DiagnosticEvents", tables);
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacyHistoryJsonOnceAndRenamesToMigratedBak()
    {
        _paths.EnsureDirectoriesExist();

        var legacyEntries = new List<DownloadHistoryEntry>
        {
            new()
            {
                Id = "legacy-1",
                VideoId = "dQw4w9WgXcQ",
                Title = "Legacy Video 1",
                Channel = "Test Channel",
                SourceUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                OutputFilePath = @"C:\Videos\Legacy1.mp4",
                QualityLabel = "1080p",
                Container = "mp4",
                FileSizeBytes = 123_456_789,
                DurationSeconds = 212.5,
                WasLivestream = false,
                CompletedAtUtc = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero)
            },
            new()
            {
                Id = "legacy-2",
                VideoId = "live1234567",
                Title = "Legacy Live Recording",
                Channel = "Live Channel",
                SourceUrl = "https://www.youtube.com/watch?v=live1234567",
                OutputFilePath = @"C:\Videos\LegacyLive.mkv",
                QualityLabel = "720p",
                Container = "mkv",
                FileSizeBytes = 500_000_000,
                DurationSeconds = 3600,
                WasLivestream = true,
                LivestreamMode = LivestreamRecordingMode.RecordFromStartToLiveEdge,
                CompletedAtUtc = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)
            }
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        await File.WriteAllTextAsync(_paths.HistoryFilePath, JsonSerializer.Serialize(legacyEntries, jsonOptions));

        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        var health = await db.InitializeAsync();

        Assert.Equal(2, health.MigratedLegacyHistoryCount);
        Assert.False(File.Exists(_paths.HistoryFilePath));
        Assert.True(File.Exists(_paths.HistoryFilePath + ".migrated.bak"));

        var historyStore = new SqliteHistoryStore(db, NullLogger<SqliteHistoryStore>.Instance);
        var loaded = await historyStore.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("legacy-2", loaded[0].Id);
        Assert.Equal(LivestreamRecordingMode.RecordFromStartToLiveEdge, loaded[0].LivestreamMode);
        Assert.Equal("legacy-1", loaded[1].Id);
        Assert.Equal(MediaIntegrityStatus.Unverified, loaded[1].IntegrityStatus);
    }

    [Fact]
    public async Task InitializeAsync_CreatesBackupBeforeMigration_AndRetainsOnlyFiveNewestBackups()
    {
        // Step 1: Initialize v1 database with a library item
        using (var dbV1 = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance))
        {
            await dbV1.InitializeAsync();
            var store = new SqliteHistoryStore(dbV1, NullLogger<SqliteHistoryStore>.Instance);
            await store.AddAsync(new DownloadHistoryEntry
            {
                Id = "item-v1",
                VideoId = "abc12345678",
                Title = "Persisted Before Upgrade",
                OutputFilePath = @"C:\Videos\BeforeUpgrade.mp4",
                FileSizeBytes = 42_000
            });
        }

        // Seed 6 older dummy backups so pruning to MaxRetainedBackups (5) is exercised
        Directory.CreateDirectory(_paths.DatabaseBackupsDirectory);
        for (var i = 1; i <= 6; i++)
        {
            var oldPath = Path.Combine(_paths.DatabaseBackupsDirectory, $"ytdownloadtool-v0-2025010{i}000000.db");
            await File.WriteAllBytesAsync(oldPath, [1, 2, 3]);
            File.SetLastWriteTimeUtc(oldPath, new DateTime(2025, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        // Step 2: Upgrade v1 -> v2 with a succeeding migration
        ISchemaMigration[] v2Migrations = [new InitialSchemaMigrationV1(), new SuccessfulV2Migration()];
        using (var dbV2 = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance, v2Migrations))
        {
            var health = await dbV2.InitializeAsync();

            Assert.Equal(DatabaseHealthStatus.Healthy, health.Status);
            Assert.True(health.IsSchemaCompatible);
            Assert.Equal(2, health.CurrentSchemaVersion);
            Assert.NotNull(health.LastBackupPath);
            Assert.True(File.Exists(health.LastBackupPath));
            Assert.Equal(SqliteDatabaseManager.MaxRetainedBackups, health.BackupCount);

            var backups = Directory.GetFiles(_paths.DatabaseBackupsDirectory, "ytdownloadtool-v*.db");
            Assert.Equal(5, backups.Length);
            Assert.Contains(backups, b => Path.GetFileName(b).StartsWith("ytdownloadtool-v1-", StringComparison.OrdinalIgnoreCase));

            // Confirm the backup itself is a valid SQLite database containing the v1 row
            var backupConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = health.LastBackupPath!,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var backupConn = new SqliteConnection(backupConnStr);
            backupConn.Open();
            Assert.Equal(1L, ExecuteScalarLong(backupConn, "PRAGMA user_version;"));
            Assert.Equal(1L, ExecuteScalarLong(backupConn, "SELECT COUNT(*) FROM LibraryItems WHERE Id = 'item-v1';"));
        }
    }

    [Fact]
    public async Task InitializeAsync_OnForcedMigrationFailure_RollsBackRestoresBackupAndDegradesSafelyWithoutCrashing()
    {
        // Step 1: Initialize v1 and insert data
        using (var dbV1 = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance))
        {
            await dbV1.InitializeAsync();
            var store = new SqliteHistoryStore(dbV1, NullLogger<SqliteHistoryStore>.Instance);
            await store.AddAsync(new DownloadHistoryEntry
            {
                Id = "keep-me",
                VideoId = "keep1234567",
                Title = "Must Survive Failed Migration",
                OutputFilePath = @"C:\Videos\KeepMe.mp4",
                FileSizeBytes = 99_999
            });
        }

        // Step 2: Run with a failing v2 migration that modifies schema before throwing
        ISchemaMigration[] failingMigrations = [new InitialSchemaMigrationV1(), new FailingV2Migration()];
        using var dbFailed = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance, failingMigrations);

        var health = await dbFailed.InitializeAsync();

        Assert.Equal(DatabaseHealthStatus.Recovered, health.Status);
        Assert.False(health.IsSchemaCompatible);
        Assert.Equal(1, health.CurrentSchemaVersion);
        Assert.Equal(2, health.TargetSchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(health.RecoveryNotice));
        Assert.NotNull(health.LastBackupPath);
        Assert.True(File.Exists(health.LastBackupPath));

        // Repositories must degrade safely (no crash / SqliteException) when schema is mismatched
        var historyRepo = new SqliteHistoryStore(dbFailed, NullLogger<SqliteHistoryStore>.Instance);
        var jobRepo = new SqliteDownloadJobRepository(dbFailed, NullLogger<SqliteDownloadJobRepository>.Instance);
        var diagRepo = new SqliteDiagnosticsRepository(dbFailed, NullLogger<SqliteDiagnosticsRepository>.Instance);

        Assert.Empty(await historyRepo.LoadAsync());
        Assert.Empty(await jobRepo.GetQueuedJobsAsync());
        Assert.Empty(await diagRepo.GetRecentFailuresAsync());
        var protectedTemp = await jobRepo.GetProtectedTempDirectoryNamesAsync();
        Assert.False(protectedTemp.CanSafelyPrune);

        // Verify the restored database file on disk still contains our v1 data intact and NOT the partial v2 table!
        using var verifyDbV1 = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        var verifyHealth = await verifyDbV1.InitializeAsync();
        Assert.Equal(DatabaseHealthStatus.Healthy, verifyHealth.Status);
        Assert.Equal(1, verifyHealth.CurrentSchemaVersion);

        var verifyHistory = new SqliteHistoryStore(verifyDbV1, NullLogger<SqliteHistoryStore>.Instance);
        var items = await verifyHistory.LoadAsync();
        Assert.Single(items);
        Assert.Equal("keep-me", items[0].Id);
        Assert.Equal("Must Survive Failed Migration", items[0].Title);
    }

    [Fact]
    public async Task DownloadJobRepository_UnifiedQueueOrdering_MoveUpDownTopAndProtectedTempFolders()
    {
        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();
        var repo = new SqliteDownloadJobRepository(db, NullLogger<SqliteDownloadJobRepository>.Instance);

        var j1 = await repo.InsertQueuedJobAsync(MakeJob("job-1", "First"));
        var j2 = await repo.InsertQueuedJobAsync(MakeJob("job-2", "Second"));
        var j3 = await repo.InsertQueuedJobAsync(MakeJob("job-3", "Third"));

        Assert.True(j1.QueueOrder < j2.QueueOrder && j2.QueueOrder < j3.QueueOrder);

        // Move job-3 to top -> [job-3, job-1, job-2]
        Assert.True(await repo.MoveQueuedJobToTopAsync("job-3"));
        var order1 = (await repo.GetQueuedJobsAsync()).Select(j => j.JobId).ToArray();
        Assert.Equal(["job-3", "job-1", "job-2"], order1);

        // Move job-2 up -> [job-3, job-2, job-1]
        Assert.True(await repo.MoveQueuedJobUpAsync("job-2"));
        var order2 = (await repo.GetQueuedJobsAsync()).Select(j => j.JobId).ToArray();
        Assert.Equal(["job-3", "job-2", "job-1"], order2);

        // Move job-3 down -> [job-2, job-3, job-1]
        Assert.True(await repo.MoveQueuedJobDownAsync("job-3"));
        var order3 = (await repo.GetQueuedJobsAsync()).Select(j => j.JobId).ToArray();
        Assert.Equal(["job-2", "job-3", "job-1"], order3);

        // Dequeue should claim job-2 atomically
        var dequeued = await repo.DequeueNextQueuedJobAsync();
        Assert.NotNull(dequeued);
        Assert.Equal("job-2", dequeued.JobId);
        Assert.Equal(DownloadJobState.Preparing, dequeued.Status);

        // Mark job-2 Completed, job-1 Paused, job-3 remains Queued
        await repo.UpdateJobStateAsync("job-2", DownloadJobState.Completed, progressPercent: 100);
        await repo.UpdateJobStateAsync("job-1", DownloadJobState.Paused, progressPercent: 42.5);

        var protectedSnap = await repo.GetProtectedTempDirectoryNamesAsync();
        Assert.True(protectedSnap.CanSafelyPrune);
        Assert.Contains("job-1", protectedSnap.ProtectedFolderNames);
        Assert.Contains("job-3", protectedSnap.ProtectedFolderNames);
        Assert.DoesNotContain("job-2", protectedSnap.ProtectedFolderNames);
    }

    [Fact]
    public async Task HistoryStoreAndDiagnosticsRepository_PersistIntegrityMetadataAndRedactSecrets()
    {
        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();

        var history = new SqliteHistoryStore(db, NullLogger<SqliteHistoryStore>.Instance);
        await history.AddAsync(new DownloadHistoryEntry
        {
            Id = "lib-1",
            VideoId = "vid12345678",
            Title = "Verified Clip",
            OutputFilePath = @"C:\Videos\Verified.mp4",
            QualityLabel = "1080p",
            Container = "mp4",
            FileSizeBytes = 10_000
        });

        var verifiedAt = new DateTimeOffset(2026, 11, 21, 1, 0, 0, TimeSpan.Zero);
        await history.UpdateIntegrityAsync(
            "lib-1",
            MediaIntegrityStatus.Repaired,
            "Repaired faststart atom",
            "1920x1080",
            "h264",
            "aac",
            10_240,
            125.4,
            verifiedAt);

        var loaded = await history.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal(MediaIntegrityStatus.Repaired, loaded[0].IntegrityStatus);
        Assert.Equal("1920x1080", loaded[0].Resolution);
        Assert.Equal("h264", loaded[0].VideoCodec);
        Assert.Equal("aac", loaded[0].AudioCodec);
        Assert.Equal(10_240, loaded[0].FileSizeBytes);
        Assert.Equal(125.4, loaded[0].DurationSeconds);

        var diag = new SqliteDiagnosticsRepository(db, NullLogger<SqliteDiagnosticsRepository>.Instance);
        await diag.RecordFailureAsync(new DiagnosticEventRecord
        {
            JobId = "job-err",
            VideoId = "vid12345678",
            Title = "Secret Test",
            ErrorCategory = "TransientNetwork",
            UserMessage = "Failed with Cookie: SID=supersecretcookie",
            RedactedTechnicalDetails = "GET https://example.com/v?token=my_secret_token&sig=abc123 Authorization: Bearer xyz987",
            ExitCode = 1,
            RetryAttempt = 3
        });

        var recent = await diag.GetRecentFailuresAsync(25);
        Assert.Single(recent);
        Assert.DoesNotContain("supersecretcookie", recent[0].UserMessage);
        Assert.DoesNotContain("my_secret_token", recent[0].RedactedTechnicalDetails);
        Assert.DoesNotContain("xyz987", recent[0].RedactedTechnicalDetails);
        Assert.Contains("[REDACTED]", recent[0].RedactedTechnicalDetails);
    }

    private PersistedDownloadJob MakeJob(string id, string title)
    {
        var quality = new QualityOption(
            Id: "video-1080",
            Label: "1080p",
            IsAudioOnly: false,
            Height: 1080,
            Fps: 30,
            CodecSummary: "H.264",
            EstimatedSizeBytes: 100_000_000,
            FormatSelector: "bv*+ba/b",
            PreferredAudioExtension: null,
            RequiresFFmpeg: true,
            SupportedContainers: ["mp4", "mkv"]);

        var req = new DownloadJobRequest(
            JobId: id,
            VideoId: "dQw4w9WgXcQ",
            SourceUrl: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            Title: title,
            Channel: "Channel",
            ThumbnailUrl: null,
            OutputDirectory: _paths.DefaultDownloadsDirectory,
            SelectedQuality: quality,
            OutputContainer: "mp4",
            LiveStatus: MediaLiveStatus.NotLive);

        return PersistedDownloadJob.FromRequest(req, Path.Combine(_paths.TempDownloadsDirectory, id));
    }

    private static string? ExecuteScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ExecuteScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class SuccessfulV2Migration : ISchemaMigration
    {
        public int TargetVersion => 2;
        public string Description => "Test migration v2";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "CREATE TABLE TestV2Marker (Id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class FailingV2Migration : ISchemaMigration
    {
        public int TargetVersion => 2;
        public string Description => "Intentional failing migration v2";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "CREATE TABLE ShouldBeRolledBack (Id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
            throw new InvalidOperationException("Simulated schema migration error.");
        }
    }
}
