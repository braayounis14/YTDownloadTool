using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Storage;

public interface ISqliteDatabaseManager : IDisposable
{
    int TargetSchemaVersion { get; }
    bool IsInitialized { get; }
    bool IsSchemaCompatible { get; }
    DatabaseHealthSnapshot Health { get; }
    Task<DatabaseHealthSnapshot> InitializeAsync(CancellationToken ct = default);
    DatabaseHealthSnapshot RefreshHealthSnapshot();
    SqliteConnection OpenConnection();
}

public sealed class SqliteDatabaseManager : ISqliteDatabaseManager
{
    public const int MaxRetainedBackups = 5;

    private static readonly JsonSerializerOptions LegacyHistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<SqliteDatabaseManager> _logger;
    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly string _connectionString;

    private DatabaseHealthSnapshot _health;
    private bool _initialized;

    public SqliteDatabaseManager(
        IAppPaths paths,
        ILogger<SqliteDatabaseManager> logger,
        IEnumerable<ISchemaMigration>? migrations = null)
    {
        _paths = paths;
        _logger = logger;

        var list = migrations?.OrderBy(m => m.TargetVersion).ToList();
        _migrations = list is { Count: > 0 } ? list : [new InitialSchemaMigrationV1()];
        TargetSchemaVersion = _migrations[^1].TargetVersion;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        _health = new DatabaseHealthSnapshot(
            Status: DatabaseHealthStatus.Healthy,
            CurrentSchemaVersion: 0,
            TargetSchemaVersion: TargetSchemaVersion,
            IsSchemaCompatible: false,
            DatabaseSizeBytes: 0,
            BackupCount: 0,
            DatabasePath: _paths.DatabaseFilePath,
            BackupsDirectory: _paths.DatabaseBackupsDirectory);
    }

    public int TargetSchemaVersion { get; }
    public bool IsInitialized => Volatile.Read(ref _initialized);
    public bool IsSchemaCompatible => _health.IsSchemaCompatible;
    public DatabaseHealthSnapshot Health => _health;

    public SqliteConnection OpenConnection()
    {
        _paths.EnsureDirectoriesExist();
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ConfigureConnectionPragmas(conn);
        return conn;
    }

    public async Task<DatabaseHealthSnapshot> InitializeAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _initialized))
            return RefreshHealthSnapshot();

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return RefreshHealthSnapshot();

            _paths.EnsureDirectoriesExist();

            var migratedLegacyCount = 0;
            string? backupPath = null;
            var currentVersion = 0;
            var status = DatabaseHealthStatus.Healthy;
            string? recoveryNotice = null;
            bool isCompatible;

            SqliteConnection? connection = null;
            try
            {
                connection = OpenConnection();
                currentVersion = ReadUserVersion(connection);

                var pending = _migrations
                    .Where(m => m.TargetVersion > currentVersion)
                    .OrderBy(m => m.TargetVersion)
                    .ToList();

                if (pending.Count > 0)
                {
                    if (ShouldCreatePreMigrationBackup(connection, currentVersion))
                    {
                        backupPath = CreatePreMigrationBackup(connection, currentVersion);
                        PruneOldBackups();
                    }

                    foreach (var migration in pending)
                    {
                        _logger.LogInformation(
                            "Applying SQLite migration v{FromVersion} -> v{ToVersion}: {Description}",
                            currentVersion, migration.TargetVersion, migration.Description);

                        using var tx = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                        try
                        {
                            migration.Apply(connection, tx);
                            SetUserVersion(connection, tx, migration.TargetVersion);
                            tx.Commit();
                            currentVersion = migration.TargetVersion;
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { }
                            throw;
                        }
                    }
                }

                if (currentVersion > TargetSchemaVersion)
                {
                    status = DatabaseHealthStatus.Recovered;
                    isCompatible = false;
                    recoveryNotice =
                        $"Database schema v{currentVersion} is newer than this version of YTDownloadTool (expects v{TargetSchemaVersion}). " +
                        "Database writes are paused safely to protect your data. Please upgrade YTDownloadTool.";
                    _logger.LogWarning("{Notice}", recoveryNotice);
                }
                else
                {
                    isCompatible = currentVersion == TargetSchemaVersion;
                    if (isCompatible)
                    {
                        migratedLegacyCount = MigrateLegacyHistoryIfPresent(connection);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SQLite database initialization/migration failed at schema v{Version}.", currentVersion);

                if (connection is not null)
                {
                    try { connection.Close(); } catch { }
                    try { connection.Dispose(); } catch { }
                    connection = null;
                }

                SqliteConnection.ClearAllPools();

                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                {
                    var restoredVersion = RestoreDatabaseFromBackup(backupPath, currentVersion);
                    currentVersion = restoredVersion;
                    status = DatabaseHealthStatus.Recovered;
                    isCompatible = false;
                    recoveryNotice =
                        $"Database upgrade to v{TargetSchemaVersion} failed ({ex.Message}) and was automatically restored from backup " +
                        $"'{Path.GetFileName(backupPath)}' (schema v{restoredVersion}). Your existing data was preserved safely.";
                    _logger.LogWarning("{Notice}", recoveryNotice);
                }
                else
                {
                    TryDeleteWalSidecars(_paths.DatabaseFilePath);
                    status = DatabaseHealthStatus.Error;
                    isCompatible = false;
                    recoveryNotice =
                        $"Database initialization failed ({ex.Message}). Running in safe recovery mode; check Diagnostics for details.";
                }
            }
            finally
            {
                if (connection is not null)
                {
                    try { connection.Close(); } catch { }
                    try { connection.Dispose(); } catch { }
                }
            }

            _health = BuildSnapshot(
                status,
                currentVersion,
                isCompatible,
                lastBackupPath: backupPath ?? FindLatestBackupPath(),
                recoveryNotice: recoveryNotice,
                migratedLegacyHistoryCount: migratedLegacyCount);

            Volatile.Write(ref _initialized, true);
            return _health;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public DatabaseHealthSnapshot RefreshHealthSnapshot()
    {
        var current = _health;
        var refreshed = BuildSnapshot(
            current.Status,
            current.CurrentSchemaVersion,
            current.IsSchemaCompatible,
            current.LastBackupPath ?? FindLatestBackupPath(),
            current.RecoveryNotice,
            current.MigratedLegacyHistoryCount);
        _health = refreshed;
        return refreshed;
    }

    public void Dispose()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(conn);
        }
        catch
        {
            // Best-effort pool cleanup on shutdown.
        }

        _initLock.Dispose();
    }

    private static void ConfigureConnectionPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static bool ShouldCreatePreMigrationBackup(SqliteConnection connection, int currentVersion)
    {
        if (currentVersion > 0)
            return true;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private string CreatePreMigrationBackup(SqliteConnection sourceConnection, int oldVersion)
    {
        Directory.CreateDirectory(_paths.DatabaseBackupsDirectory);

        using (var checkpointCmd = sourceConnection.CreateCommand())
        {
            checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpointCmd.ExecuteNonQuery();
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(_paths.DatabaseBackupsDirectory, $"ytdownloadtool-v{oldVersion}-{stamp}.db");
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(_paths.DatabaseBackupsDirectory, $"ytdownloadtool-v{oldVersion}-{stamp}-{suffix++}.db");
        }

        var backupConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using (var backupConnection = new SqliteConnection(backupConnStr))
        {
            backupConnection.Open();
            sourceConnection.BackupDatabase(backupConnection);
            backupConnection.Close();
        }

        _logger.LogInformation("Created pre-migration SQLite backup at {BackupPath}.", backupPath);
        return backupPath;
    }

    private void PruneOldBackups()
    {
        try
        {
            if (!Directory.Exists(_paths.DatabaseBackupsDirectory))
                return;

            var backups = new DirectoryInfo(_paths.DatabaseBackupsDirectory)
                .EnumerateFiles("ytdownloadtool-v*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var obsolete in backups.Skip(MaxRetainedBackups))
            {
                try
                {
                    obsolete.Delete();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete old backup {BackupFile}.", obsolete.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed pruning old SQLite backup files.");
        }
    }

    private int RestoreDatabaseFromBackup(string backupPath, int expectedVersion)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            TryDeleteWalSidecars(_paths.DatabaseFilePath);

            var tempRestorePath = $"{_paths.DatabaseFilePath}.{Guid.NewGuid():N}.restore";
            File.Copy(backupPath, tempRestorePath, overwrite: true);
            File.Move(tempRestorePath, _paths.DatabaseFilePath, overwrite: true);
            TryDeleteWalSidecars(_paths.DatabaseFilePath);

            var verifyConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabaseFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var verifyConn = new SqliteConnection(verifyConnStr);
            verifyConn.Open();
            return ReadUserVersion(verifyConn);
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(restoreEx, "Failed while restoring SQLite backup '{BackupPath}'.", backupPath);
            return expectedVersion;
        }
    }

    private int MigrateLegacyHistoryIfPresent(SqliteConnection connection)
    {
        var historyPath = _paths.HistoryFilePath;
        if (!File.Exists(historyPath))
            return 0;

        List<DownloadHistoryEntry>? entries;
        try
        {
            var json = File.ReadAllText(historyPath);
            entries = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json, LegacyHistoryJsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Legacy history.json could not be parsed; archiving as corrupt backup.");
            TryRenameFile(historyPath, historyPath + ".corrupt.bak");
            return 0;
        }

        var importedCount = 0;
        using (var tx = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO LibraryItems (
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

            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pVideoId = cmd.Parameters.Add("$videoId", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pChannel = cmd.Parameters.Add("$channel", SqliteType.Text);
            var pSourceUrl = cmd.Parameters.Add("$sourceUrl", SqliteType.Text);
            var pOutputPath = cmd.Parameters.Add("$outputFilePath", SqliteType.Text);
            var pThumb = cmd.Parameters.Add("$thumbnailUrl", SqliteType.Text);
            var pQuality = cmd.Parameters.Add("$qualityLabel", SqliteType.Text);
            var pContainer = cmd.Parameters.Add("$container", SqliteType.Text);
            var pResolution = cmd.Parameters.Add("$resolution", SqliteType.Text);
            var pVCodec = cmd.Parameters.Add("$videoCodec", SqliteType.Text);
            var pACodec = cmd.Parameters.Add("$audioCodec", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$fileSizeBytes", SqliteType.Integer);
            var pDuration = cmd.Parameters.Add("$durationSeconds", SqliteType.Real);
            var pLive = cmd.Parameters.Add("$wasLivestream", SqliteType.Integer);
            var pLiveMode = cmd.Parameters.Add("$livestreamMode", SqliteType.Integer);
            var pIntegrity = cmd.Parameters.Add("$integrityStatus", SqliteType.Text);
            var pIntegrityMsg = cmd.Parameters.Add("$integrityMessage", SqliteType.Text);
            var pCompleted = cmd.Parameters.Add("$completedAtUtc", SqliteType.Text);
            var pVerified = cmd.Parameters.Add("$verifiedAtUtc", SqliteType.Text);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.OutputFilePath) && string.IsNullOrWhiteSpace(entry.Title))
                    continue;

                pId.Value = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
                pVideoId.Value = entry.VideoId ?? string.Empty;
                pTitle.Value = entry.Title ?? string.Empty;
                pChannel.Value = entry.Channel ?? string.Empty;
                pSourceUrl.Value = entry.SourceUrl ?? string.Empty;
                pOutputPath.Value = entry.OutputFilePath ?? string.Empty;
                pThumb.Value = (object?)entry.ThumbnailUrl ?? DBNull.Value;
                pQuality.Value = entry.QualityLabel ?? string.Empty;
                pContainer.Value = entry.Container ?? string.Empty;
                pResolution.Value = (object?)entry.Resolution ?? DBNull.Value;
                pVCodec.Value = (object?)entry.VideoCodec ?? DBNull.Value;
                pACodec.Value = (object?)entry.AudioCodec ?? DBNull.Value;
                pSize.Value = entry.FileSizeBytes;
                pDuration.Value = entry.DurationSeconds.HasValue ? entry.DurationSeconds.Value : DBNull.Value;
                pLive.Value = entry.WasLivestream ? 1 : 0;
                pLiveMode.Value = entry.LivestreamMode.HasValue ? (int)entry.LivestreamMode.Value : DBNull.Value;
                pIntegrity.Value = entry.IntegrityStatus.ToString();
                pIntegrityMsg.Value = (object?)entry.IntegrityMessage ?? DBNull.Value;
                pCompleted.Value = entry.CompletedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                pVerified.Value = entry.VerifiedAtUtc.HasValue
                    ? entry.VerifiedAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : DBNull.Value;

                importedCount += cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var migratedBackupPath = historyPath + ".migrated.bak";
        TryRenameFile(historyPath, migratedBackupPath);
        _logger.LogInformation(
            "Migrated {Count} legacy library entries from history.json to SQLite and archived to {BackupPath}.",
            importedCount, migratedBackupPath);
        return importedCount;
    }

    private void TryRenameFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename '{Source}' to '{Destination}'.", source, destination);
        }
    }

    private static void TryDeleteWalSidecars(string dbPath)
    {
        foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(sidecar))
                    File.Delete(sidecar);
            }
            catch
            {
                // Best effort
            }
        }
    }

    private DatabaseHealthSnapshot BuildSnapshot(
        DatabaseHealthStatus status,
        int currentVersion,
        bool isCompatible,
        string? lastBackupPath,
        string? recoveryNotice,
        int migratedLegacyHistoryCount)
    {
        long dbSize = 0;
        try
        {
            if (File.Exists(_paths.DatabaseFilePath))
                dbSize += new FileInfo(_paths.DatabaseFilePath).Length;
            if (File.Exists(_paths.DatabaseFilePath + "-wal"))
                dbSize += new FileInfo(_paths.DatabaseFilePath + "-wal").Length;
        }
        catch
        {
            // Ignore transient IO issues when reading size
        }

        var backupCount = 0;
        try
        {
            if (Directory.Exists(_paths.DatabaseBackupsDirectory))
            {
                backupCount = Directory.EnumerateFiles(_paths.DatabaseBackupsDirectory, "ytdownloadtool-v*.db").Count();
            }
        }
        catch
        {
            // Ignore transient IO issues
        }

        return new DatabaseHealthSnapshot(
            Status: status,
            CurrentSchemaVersion: currentVersion,
            TargetSchemaVersion: TargetSchemaVersion,
            IsSchemaCompatible: isCompatible,
            DatabaseSizeBytes: dbSize,
            BackupCount: backupCount,
            DatabasePath: _paths.DatabaseFilePath,
            BackupsDirectory: _paths.DatabaseBackupsDirectory,
            LastBackupPath: lastBackupPath,
            RecoveryNotice: recoveryNotice,
            MigratedLegacyHistoryCount: migratedLegacyHistoryCount);
    }

    private string? FindLatestBackupPath()
    {
        try
        {
            if (!Directory.Exists(_paths.DatabaseBackupsDirectory))
                return null;

            return new DirectoryInfo(_paths.DatabaseBackupsDirectory)
                .EnumerateFiles("ytdownloadtool-v*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
