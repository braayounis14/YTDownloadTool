using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Platform;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Platform;

public sealed class StoreComplianceTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly DefaultAppPaths _paths;

    public StoreComplianceTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ytdl_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _paths = new DefaultAppPaths(
            appDirectory: Path.Combine(_sandboxRoot, "app"),
            localAppData: Path.Combine(_sandboxRoot, "local"),
            roamingAppData: Path.Combine(_sandboxRoot, "roaming"),
            videosFolder: Path.Combine(_sandboxRoot, "videos"));
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandboxRoot)) Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public void AppPaths_WhenMsixPackaged_ResolvesPhysicalPackageLocalCache_AndPreservesLegacyUnpackagedPaths()
    {
        var localBase = Path.Combine(_sandboxRoot, "local");
        var roamingBase = Path.Combine(_sandboxRoot, "roaming");
        var videosBase = Path.Combine(_sandboxRoot, "videos");

        const string pfn = "BaraaYounis.Tidejar_1234567890abc";
        var msixPaths = new DefaultAppPaths(
            appDirectory: Path.Combine(_sandboxRoot, "pkg"),
            localAppData: localBase,
            roamingAppData: roamingBase,
            videosFolder: videosBase,
            msixPackageFamilyName: pfn);

        Assert.Equal(Path.Combine(localBase, "Packages", pfn, "LocalCache", "Local", "Tidejar"), msixPaths.DataRootDirectory);
        Assert.Equal(Path.Combine(localBase, "Packages", pfn, "LocalCache", "Roaming", "Tidejar"), msixPaths.SettingsRootDirectory);
        Assert.Equal(Path.Combine(videosBase, "Tidejar"), msixPaths.DefaultDownloadsDirectory);
        Assert.EndsWith("PRIVACY.md", msixPaths.PrivacyPolicyFilePath);
        Assert.EndsWith("THIRD_PARTY_NOTICES.txt", msixPaths.ThirdPartyNoticesFilePath);

        var legacyPaths = new DefaultAppPaths(
            appDirectory: Path.Combine(_sandboxRoot, "pkg"),
            localAppData: localBase,
            roamingAppData: roamingBase,
            videosFolder: videosBase);

        Assert.Equal(Path.Combine(localBase, "YTDownloadToolData"), legacyPaths.DataRootDirectory);
        Assert.Equal(Path.Combine(roamingBase, "YTDownloadTool"), legacyPaths.SettingsRootDirectory);
    }

    [Fact]
    public void PackageIdentityHelper_InUnpackagedHost_ReturnsFalseWithoutThrowing()
    {
        Assert.False(PackageIdentityHelper.IsPackagedMsix());
        Assert.Null(PackageIdentityHelper.TryGetPackageFamilyName());
    }

    [Fact]
    public async Task ToolManager_InStoreMode_RunsDirectlyFromReadOnlyBaseline_WithZeroNetworkDownloads()
    {
        SeedFakeBaseline("yt-dlp", ("yt-dlp.exe", "ytdlp-store"));
        SeedFakeBaseline("ffmpeg", ("ffmpeg.exe", "ffmpeg-store"), ("ffprobe.exe", "ffprobe-store"), ("LICENSE.txt", "GPLv3"));
        SeedFakeBaseline("deno", ("deno.exe", "deno-store"));

        var networkCalls = 0;
        var failingHttpHandler = new CountingHttpHandler(() =>
        {
            networkCalls++;
            throw new InvalidOperationException("Store mode MUST NEVER make network calls for tool updates or downloads.");
        });

        var runner = new FakeToolRunner(exePath =>
        {
            var content = File.ReadAllText(exePath);
            return content switch
            {
                "ytdlp-store" => (0, "2026.10.26"),
                "ffmpeg-store" => (0, "ffmpeg version N-128970-g11337618b2-20261116 Copyright (c) FFmpeg"),
                "ffprobe-store" => (0, "ffprobe version N-128970-g11337618b2-20261116"),
                "deno-store" => (0, "deno 2.9.8 (stable, release, x86_64-pc-windows-msvc)"),
                _ => (1, string.Empty)
            };
        });

        var settingsStore = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        using var httpClient = new HttpClient(failingHttpHandler);
        var storeProfile = DistributionProfile.ForTesting(isStoreManaged: true, isPackagedMsix: true);
        var manager = new ToolManager(_paths, settingsStore, runner, httpClient, NullLogger<ToolManager>.Instance, storeProfile);

        // 1. EnsureToolsReady runs strictly from read-only BaselineTools (no copy into Tools\, no HTTP calls)
        var tc = await manager.EnsureToolsReadyAsync();
        Assert.StartsWith(_paths.BaselineToolsDirectory, tc.YtDlpPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_paths.BaselineToolsDirectory, tc.FFmpegPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_paths.BaselineToolsDirectory, tc.DenoPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_paths.ToolsManifestPath), "Store mode should not copy mutable tools or write a mutable manifest.");
        Assert.Equal(0, networkCalls);

        // 2. Update checks return Store-managed with zero HTTP calls
        var singleCheck = await manager.CheckForUpdateAsync(ToolKind.YtDlp, force: true);
        Assert.False(singleCheck.UpdateAvailable);
        Assert.Contains("Microsoft Store", singleCheck.Message);

        var allChecks = await manager.CheckAllForUpdatesAsync(force: true);
        Assert.Equal(3, allChecks.Count);
        Assert.All(allChecks, c => Assert.False(c.UpdateAvailable));
        Assert.Equal(0, networkCalls);

        // 3. Statuses show bundled Ready and CanRollback=false
        var statuses = await manager.GetStatusesAsync();
        Assert.All(statuses, s =>
        {
            Assert.Equal(ToolHealthState.Ready, s.State);
            Assert.False(s.UpdateAvailable);
            Assert.False(s.CanRollback);
            Assert.Contains("Microsoft Store", s.Message);
        });

        // 4. Runtime download / rollback MUST be blocked by NotSupportedException (Policy 10.2.2 & 10.2.3)
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.UpdateToolAsync(ToolKind.YtDlp));
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.UpdateToolAsync(ToolKind.FFmpeg));
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.RollbackToolAsync(ToolKind.YtDlp));
        Assert.Equal(0, networkCalls);

        // 5. Repair re-verifies bundled tools without network
        var repaired = await manager.RepairToolAsync(ToolKind.FFmpeg);
        Assert.Equal(ToolHealthState.Ready, repaired.State);
        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public async Task ToolManager_InStoreMode_MissingBundledTool_FailsCleanlyWithoutDownloadingFromInternet()
    {
        // Only yt-dlp staged, FFmpeg missing
        SeedFakeBaseline("yt-dlp", ("yt-dlp.exe", "ytdlp-store"));

        var networkCalls = 0;
        var failingHttp = new CountingHttpHandler(() =>
        {
            networkCalls++;
            throw new InvalidOperationException("Store mode must never download missing binaries from GitHub.");
        });

        var runner = new FakeToolRunner(exePath => File.ReadAllText(exePath) == "ytdlp-store" ? (0, "2026.10.26") : (1, ""));
        var settingsStore = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        using var httpClient = new HttpClient(failingHttp);
        var manager = new ToolManager(_paths, settingsStore, runner, httpClient, NullLogger<ToolManager>.Instance, DistributionProfile.ForTesting(isStoreManaged: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureToolsReadyAsync());
        Assert.Contains("Microsoft Store", ex.Message);
        Assert.Equal(0, networkCalls);
    }

    [Theory]
    [InlineData("ERROR: [youtube] xyz: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>")]
    [InlineData("ERROR: Failed to resolve 'www.youtube.com'")]
    [InlineData("ERROR: Network is unreachable")]
    [InlineData("ERROR: No such host is known")]
    public void ErrorClassifier_OfflineDnsAndUnreachableErrors_ProduceClearOfflineMessage(string stdErr)
    {
        var classified = YtDlpErrorClassifier.Classify(stdErr, 1);
        Assert.Equal(DownloadErrorCategory.TransientNetwork, classified.Category);
        Assert.Contains("No internet connection", classified.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MediaProbeService_WhenConnectionHangsOffline_TimesOutWithClearMessage()
    {
        var hangingRunner = new HangingProcessRunner();
        var stubToolManager = new StubToolManager(new ResolvedToolchain(
            @"C:\fake\yt-dlp.exe", @"C:\fake", @"C:\fake\ffmpeg.exe", @"C:\fake\ffprobe.exe", @"C:\fake\deno.exe",
            "2026.10.26", "8.1", "2.9.8"));

        var probe = new MediaProbeService(
            stubToolManager,
            hangingRunner,
            NullLogger<MediaProbeService>.Instance,
            probeTimeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            probe.ProbeAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));

        Assert.Contains("Timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internet connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivacyPolicyAndThirdPartyNotices_ExistWithRequiredStoreDisclosures()
    {
        var profile = DistributionProfile.ForTesting(isStoreManaged: true, isPackagedMsix: true);
        Assert.Equal("Tidejar - Video & Stream Saver", profile.AppDisplayName);
        Assert.Equal("https://github.com/braayounis14/YTDownloadTool/blob/main/PRIVACY.md", profile.PrivacyPolicyUrl);

        // Locate repo root relative to test assembly
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PRIVACY.md")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var privacyText = File.ReadAllText(Path.Combine(dir!.FullName, "PRIVACY.md"));
        Assert.Contains("Tidejar", privacyText);
        Assert.Contains("Baraa Younis", privacyText);
        Assert.Contains("telemetry", privacyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLite", privacyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft Store", privacyText, StringComparison.OrdinalIgnoreCase);

        var noticesText = File.ReadAllText(Path.Combine(dir.FullName, "THIRD_PARTY_NOTICES.txt"));
        Assert.Contains("Tidejar", noticesText);
        Assert.Contains("not affiliated with", noticesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GPLv3", noticesText);
        Assert.Contains("yt-dlp", noticesText);
        Assert.Contains("Deno", noticesText);
    }

    private void SeedFakeBaseline(string folder, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_paths.BaselineToolsDirectory, folder);
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
    }

    private sealed class CountingHttpHandler(Func<HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler());
    }

    private sealed class FakeToolRunner(Func<string, (int ExitCode, string StdOut)> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            var (code, stdout) = handler(request.ExecutablePath);
            return Task.FromResult(new ProcessRunResult(code, false, false, false, TimeSpan.FromMilliseconds(5), stdout, string.Empty));
        }

        public void Dispose() { }
    }

    private sealed class HangingProcessRunner : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromSeconds(10), "", "");
        }

        public void Dispose() { }
    }

    private sealed class StubToolManager(ResolvedToolchain tc) : IToolManager
    {
        public Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default) => Task.FromResult(tc);
        public Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolStatusInfo>>([]);
        public Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
