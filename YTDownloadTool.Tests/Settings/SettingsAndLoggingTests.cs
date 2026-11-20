using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Tests.Settings;

public sealed class SettingsAndLoggingTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly DefaultAppPaths _paths;

    public SettingsAndLoggingTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ytdl_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _paths = new DefaultAppPaths(
            appDirectory: Path.Combine(_sandboxRoot, "app"),
            localAppData: Path.Combine(_sandboxRoot, "local"),
            roamingAppData: Path.Combine(_sandboxRoot, "roaming"),
            videosFolder: Path.Combine(_sandboxRoot, "videos"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task SettingsStore_PersistsAndRecoversFromCorruptedFile()
    {
        var store = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        var loaded = await store.LoadAsync();
        Assert.Equal(30, loaded.LiveEdgeMarginSeconds);
        Assert.Equal(12, loaded.AppUpdateCheckIntervalHours);

        loaded.LiveEdgeMarginSeconds = 60;
        loaded.DefaultContainer = "mkv";
        loaded.YtDlpChannel = "nightly";
        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync();
        Assert.Equal(60, reloaded.LiveEdgeMarginSeconds);
        Assert.Equal("mkv", reloaded.DefaultContainer);
        Assert.Equal("nightly", reloaded.YtDlpChannel);

        // Corrupt the JSON and verify recovery + backup creation
        await File.WriteAllTextAsync(_paths.SettingsFilePath, "{ broken json !!!");
        var recovered = await store.LoadAsync();
        Assert.Equal(30, recovered.LiveEdgeMarginSeconds);
        Assert.NotEmpty(Directory.GetFiles(_paths.SettingsRootDirectory, "*.corrupt.*.bak"));
    }

    [Fact]
    public async Task HistoryStore_AddsRemovesAndClearsEntries()
    {
        var store = new HistoryStore(_paths, NullLogger<HistoryStore>.Instance);
        var entry = new DownloadHistoryEntry
        {
            VideoId = "dQw4w9WgXcQ",
            Title = "Test Video",
            OutputFilePath = @"C:\Videos\test.mp4",
            QualityLabel = "1080p",
            Container = "mp4"
        };

        await store.AddAsync(entry);
        var items = await store.LoadAsync();
        Assert.Single(items);
        Assert.Equal("Test Video", items[0].Title);

        await store.RemoveAsync(entry.Id);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public void SensitiveDataRedactor_RedactsSecretsInLogs()
    {
        const string raw = "Request Cookie: SID=abc123; Authorization: Bearer secret_tok https://yt.example/watch?v=1&po_token=xyz999&sig=secret";
        var redacted = SensitiveDataRedactor.Redact(raw);

        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("secret_tok", redacted);
        Assert.DoesNotContain("xyz999", redacted);
        Assert.DoesNotContain("sig=secret", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RollingFileLoggerProvider_WritesRedactedEntries()
    {
        using var provider = new RollingFileLoggerProvider(_paths.LogsDirectory, LogLevel.Information);
        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Download started Authorization: Bearer my_secret_token");

        var logFiles = Directory.GetFiles(_paths.LogsDirectory, "ytdownloadtool-*.log");
        Assert.Single(logFiles);
        var content = File.ReadAllText(logFiles[0]);
        Assert.Contains("Download started", content);
        Assert.DoesNotContain("my_secret_token", content);
    }
}
