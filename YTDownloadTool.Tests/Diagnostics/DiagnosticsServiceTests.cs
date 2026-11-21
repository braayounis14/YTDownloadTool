using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Diagnostics;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Diagnostics;

public sealed class DiagnosticsServiceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly DefaultAppPaths _paths;

    public DiagnosticsServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"ytdl_diag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
        _paths = new DefaultAppPaths(
            Path.Combine(_rootDir, "app"),
            Path.Combine(_rootDir, "local"),
            Path.Combine(_rootDir, "roaming"),
            Path.Combine(_rootDir, "videos"));
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CleanUnusedTempAsync_ProtectsPausedAndRecoverableJobFolders_CleansOnlyUnused()
    {
        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();
        var jobRepo = new SqliteDownloadJobRepository(db, NullLogger<SqliteDownloadJobRepository>.Instance);
        var diagRepo = new SqliteDiagnosticsRepository(db, NullLogger<SqliteDiagnosticsRepository>.Instance);
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var ledger = new DiskSpaceReservationLedger(_ => 500L * 1024 * 1024 * 1024);

        var pausedDir = Path.Combine(_paths.TempDownloadsDirectory, "paused-vod");
        var liveDir = Path.Combine(_paths.TempDownloadsDirectory, "live-recoverable");
        var orphanDir = Path.Combine(_paths.TempDownloadsDirectory, "orphan-stale");
        Directory.CreateDirectory(pausedDir);
        Directory.CreateDirectory(liveDir);
        Directory.CreateDirectory(orphanDir);

        await File.WriteAllBytesAsync(Path.Combine(pausedDir, "a.part"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(liveDir, "b.mkv.part"), new byte[8192]);
        await File.WriteAllBytesAsync(Path.Combine(orphanDir, "trash.part"), new byte[16_384]);

        var q = new QualityOption("video-1080", "1080p", false, 1080, 30, "H.264", null, "bv*+ba/b", null, true, ["mp4"]);
        await jobRepo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
            new DownloadJobRequest("paused-vod", "v1", "https://www.youtube.com/watch?v=v1", "Paused", "C", null, _paths.DefaultDownloadsDirectory, q, "mp4", MediaLiveStatus.NotLive),
            pausedDir) with { Status = DownloadJobState.Paused });
        await jobRepo.InsertQueuedJobAsync(PersistedDownloadJob.FromRequest(
            new DownloadJobRequest("live-recoverable", "v2", "https://www.youtube.com/watch?v=v2", "Live", "C", null, _paths.DefaultDownloadsDirectory, q, "mkv", MediaLiveStatus.IsLive, LivestreamRecordingMode.RecordFromNow),
            liveDir) with { Status = DownloadJobState.InterruptedLivestream });

        var svc = new DiagnosticsService(_paths, db, jobRepo, diagRepo, settings, new StubToolManager(), ledger, NullLogger<DiagnosticsService>.Instance);

        var before = await svc.GetSummaryAsync();
        Assert.Equal(4096 + 8192, before.TempStorage.ProtectedBytes);
        Assert.Equal(16_384, before.TempStorage.ReclaimableUnusedBytes);
        Assert.Equal(2, before.TempStorage.ProtectedFolderCount);
        Assert.Equal(1, before.TempStorage.UnusedFolderCount);

        var cleaned = await svc.CleanUnusedTempAsync();
        Assert.False(cleaned.SkippedDueToRecoveryMode);
        Assert.Equal(1, cleaned.DeletedFolders);
        Assert.Equal(16_384, cleaned.ReclaimedBytes);
        Assert.True(Directory.Exists(pausedDir));
        Assert.True(Directory.Exists(liveDir));
        Assert.False(Directory.Exists(orphanDir));
    }

    [Fact]
    public async Task ExportDiagnosticBundleAsync_CreatesValidZipWithExpectedEntriesAndRedactsAllSecrets()
    {
        using var db = new SqliteDatabaseManager(_paths, NullLogger<SqliteDatabaseManager>.Instance);
        await db.InitializeAsync();
        var jobRepo = new SqliteDownloadJobRepository(db, NullLogger<SqliteDownloadJobRepository>.Instance);
        var diagRepo = new SqliteDiagnosticsRepository(db, NullLogger<SqliteDiagnosticsRepository>.Instance);
        var settings = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settings.LoadAsync();
        var ledger = new DiskSpaceReservationLedger(_ => 500L * 1024 * 1024 * 1024);

        // Seed log file containing sensitive auth/cookie/query secrets AND Windows user profile paths
        var logPath = Path.Combine(_paths.LogsDirectory, "ytdownloadtool-20261121.log");
        await File.WriteAllTextAsync(logPath,
            "2026-11-21 INFO Startup ok\r\n" +
            "2026-11-21 INFO Output saved to C:\\Users\\AliceSmith\\Videos\\YTDownloadTool\\clip.mp4\r\n" +
            "2026-11-21 INFO Temp dir D:/Users/BobJones/AppData/Local/YTDownloadToolData/Temp\r\n" +
            "2026-11-21 WARN Cookie: SAPISID=ultra_secret_cookie_val; Authorization: Bearer top_secret_jwt_123\r\n" +
            "2026-11-21 ERR GET https://rr1.googlevideo.com/videoplayback?id=1&sig=hidden_sig_999&po_token=hidden_po_888\r\n");

        // Seed tools manifest.json
        await File.WriteAllTextAsync(_paths.ToolsManifestPath, """{ "schemaVersion": 1, "tools": {} }""");

        // Seed 30 failed jobs (bundle must include only latest 25, all redacted)
        for (var i = 1; i <= 30; i++)
        {
            await diagRepo.RecordFailureAsync(new DiagnosticEventRecord
            {
                JobId = $"job-{i}",
                VideoId = $"vid{i}",
                Title = $"Video {i}",
                ErrorCategory = "TransientNetwork",
                UserMessage = $"Failed #{i} with Bearer secret_bearer_{i}",
                RedactedTechnicalDetails = $"SAPISID=cookie_val_{i} https://example.com?token=secret_tok_{i}",
                ExitCode = 1,
                RetryAttempt = 3,
                OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30 + i)
            });
        }

        var svc = new DiagnosticsService(_paths, db, jobRepo, diagRepo, settings, new StubToolManager(), ledger, NullLogger<DiagnosticsService>.Instance);

        var zipPath = Path.Combine(_rootDir, "bundle.zip");
        var export = await svc.ExportDiagnosticBundleAsync(zipPath, "1.1.1");

        Assert.True(File.Exists(export.ZipFilePath));
        Assert.Equal(1, export.IncludedLogFiles);
        Assert.Equal(25, export.IncludedFailedJobs);
        Assert.True(export.ZipSizeBytes > 0);

        using var archive = ZipFile.OpenRead(zipPath);
        var entryNames = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("logs/ytdownloadtool-20261121.log", entryNames);
        Assert.Contains("tools/manifest.json", entryNames);
        Assert.Contains("tools/versions.json", entryNames);
        Assert.Contains("system-info.json", entryNames);
        Assert.Contains("failed-jobs.json", entryNames);

        // Verify zero secrets leaked anywhere across all zip entries
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("ultra_secret_cookie_val", text);
            Assert.DoesNotContain("top_secret_jwt_123", text);
            Assert.DoesNotContain("hidden_sig_999", text);
            Assert.DoesNotContain("hidden_po_888", text);
            Assert.DoesNotContain("secret_bearer_", text);
            Assert.DoesNotContain("cookie_val_", text);
            Assert.DoesNotContain("secret_tok_", text);
            Assert.DoesNotContain("AliceSmith", text);
            Assert.DoesNotContain("BobJones", text);
        }

        // Verify Windows user profile paths are scrubbed to <REDACTED> in exported logs
        using (var logReader = new StreamReader(archive.GetEntry("logs/ytdownloadtool-20261121.log")!.Open()))
        {
            var exportedLog = await logReader.ReadToEndAsync();
            Assert.Contains(@"C:\Users\<REDACTED>\Videos\YTDownloadTool\clip.mp4", exportedLog);
            Assert.Contains("D:/Users/<REDACTED>/AppData/Local/YTDownloadToolData/Temp", exportedLog);
        }

        // Verify failed-jobs.json has exactly 25 entries
        using (var failedReader = new StreamReader(archive.GetEntry("failed-jobs.json")!.Open()))
        {
            var failedDoc = JsonDocument.Parse(await failedReader.ReadToEndAsync());
            Assert.Equal(25, failedDoc.RootElement.GetArrayLength());
        }

        // Verify system-info.json contains version and DB schema version
        using (var sysReader = new StreamReader(archive.GetEntry("system-info.json")!.Open()))
        {
            var sysDoc = JsonDocument.Parse(await sysReader.ReadToEndAsync());
            Assert.Equal("1.1.1", sysDoc.RootElement.GetProperty("appVersion").GetString());
            Assert.Equal(1, sysDoc.RootElement.GetProperty("database").GetProperty("currentSchemaVersion").GetInt32());
        }
    }

    private sealed class StubToolManager : IToolManager
    {
        public Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolStatusInfo>>([
                new ToolStatusInfo(ToolKind.YtDlp, "yt-dlp", ToolHealthState.Ready, "2026.10.26", null, null, false, false, @"C:\Tools\yt-dlp.exe", null, "Ready"),
                new ToolStatusInfo(ToolKind.FFmpeg, "FFmpeg", ToolHealthState.Ready, "8.1", null, null, false, false, @"C:\Tools\ffmpeg.exe", null, "Ready"),
                new ToolStatusInfo(ToolKind.Deno, "Deno", ToolHealthState.Ready, "2.9.8", null, null, false, false, @"C:\Tools\deno.exe", null, "Ready")
            ]);
        public Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
