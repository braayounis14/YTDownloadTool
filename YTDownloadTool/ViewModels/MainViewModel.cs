using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Platform;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public enum AppNavigationTab
{
    Downloads = 0,
    Library = 1,
    Settings = 2
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IToolManager _toolManager;
    private readonly ISettingsStore _settingsStore;
    private readonly ISqliteDatabaseManager _dbManager;
    private readonly IDownloadOrchestrator _orchestrator;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IDistributionProfile _distributionProfile;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        DownloadsViewModel downloadsViewModel,
        LibraryViewModel libraryViewModel,
        SettingsViewModel settingsViewModel,
        IToolManager toolManager,
        ISettingsStore settingsStore,
        ISqliteDatabaseManager dbManager,
        IDownloadOrchestrator orchestrator,
        IAppUpdateService appUpdateService,
        IDistributionProfile distributionProfile,
        ILogger<MainViewModel> logger)
    {
        Downloads = downloadsViewModel;
        Library = libraryViewModel;
        Settings = settingsViewModel;
        _toolManager = toolManager;
        _settingsStore = settingsStore;
        _dbManager = dbManager;
        _orchestrator = orchestrator;
        _appUpdateService = appUpdateService;
        _distributionProfile = distributionProfile;
        _logger = logger;

        AppVersionLabel = _distributionProfile.IsStoreManaged
            ? $"v{_appUpdateService.CurrentVersion} • Microsoft Store"
            : $"v{_appUpdateService.CurrentVersion}";

        Downloads.DownloadCompleted += async (_, _) => await Library.RefreshAsync();
        Settings.SettingsSaved += (_, _) =>
        {
            _orchestrator.UpdateMaxConcurrency(_settingsStore.Current.MaxConcurrentDownloads);
            Downloads.RefreshSettingsFromStore();
        };
        _appUpdateService.StateChanged += (_, state) =>
        {
            IsAppUpdateReady = state.IsDownloadedReadyToRestart;
            AppUpdateBannerText = state.IsDownloadedReadyToRestart
                ? $"Update v{state.TargetVersion} ready"
                : string.Empty;
        };
    }

    public DownloadsViewModel Downloads { get; }
    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }

    public string AppVersionLabel { get; }
    public bool HasActiveDownloads => _orchestrator.ActiveJobCount > 0;

    [ObservableProperty] private AppNavigationTab _selectedTab = AppNavigationTab.Downloads;
    [ObservableProperty] private string _engineStatusText = "Checking engine...";
    [ObservableProperty] private string _engineStatusColor = "#D29922";
    [ObservableProperty] private bool _isAppUpdateReady;
    [ObservableProperty] private string _appUpdateBannerText = string.Empty;
    [ObservableProperty] private string? _databaseRecoveryNotice;

    public bool IsDownloadsSelected => SelectedTab == AppNavigationTab.Downloads;
    public bool IsLibrarySelected => SelectedTab == AppNavigationTab.Library;
    public bool IsSettingsSelected => SelectedTab == AppNavigationTab.Settings;

    partial void OnSelectedTabChanged(AppNavigationTab value)
    {
        OnPropertyChanged(nameof(IsDownloadsSelected));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    public async Task InitializeAsync()
    {
        await _settingsStore.LoadAsync();
        _orchestrator.UpdateMaxConcurrency(_settingsStore.Current.MaxConcurrentDownloads);
        Settings.ReloadFieldsFromStore();

        // 1. Initialize SQLite database (with transactional migrations, pre-migration backup, and one-time history.json import)
        var dbHealth = await _dbManager.InitializeAsync();
        if (!string.IsNullOrWhiteSpace(dbHealth.RecoveryNotice))
        {
            DatabaseRecoveryNotice = dbHealth.RecoveryNotice;
        }

        // 2. Recover interrupted VODs & livestreams and restore persisted queue BEFORE pruning orphaned temp folders
        await Downloads.InitializeFromPersistentQueueAsync();
        await _orchestrator.PruneOrphanedTempDirectoriesAsync();

        Downloads.RefreshSettingsFromStore();
        await Library.RefreshAsync();

        // 3. Ensure multimedia toolchain (yt-dlp, FFmpeg, Deno) is healthy / seeded / ready
        var statusProgress = new Progress<string>(msg => EngineStatusText = msg);
        try
        {
            var toolchain = await _toolManager.EnsureToolsReadyAsync(statusProgress);
            EngineStatusText = _distributionProfile.IsStoreManaged
                ? $"Engine ready (Bundled) • yt-dlp {toolchain.YtDlpVersion}"
                : $"Engine ready • yt-dlp {toolchain.YtDlpVersion}";
            EngineStatusColor = "#2EA043";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial multimedia engine readiness check failed.");
            EngineStatusText = _distributionProfile.IsStoreManaged
                ? "Bundled engine error (reinstall app)"
                : "Engine needs repair (open Settings)";
            EngineStatusColor = "#DA3633";
        }

        await Settings.RefreshAllAsync();

        // Store builds (Policies 10.2.2, 10.2.3, 10.2.5) perform zero background update checks or binary downloads.
        if (!_distributionProfile.IsStoreManaged)
        {
            // 4. Background independent tool update check (respects per-tool cadences: yt-dlp 24h, FFmpeg/Deno 30d)
            if (_settingsStore.Current.AutoUpdateTools)
            {
                _ = RunBackgroundToolUpdatesAsync();
            }

            // 5. Background rate-limited Velopack app update check (throttled >= 12h, ignores HTTP 403 silently)
            _ = _appUpdateService.CheckAndDownloadIfAvailableAsync(isManualCheck: false);
        }
    }

    private async Task RunBackgroundToolUpdatesAsync()
    {
        try
        {
            var checks = await _toolManager.CheckAllForUpdatesAsync(force: false);
            var ytDlpCheck = checks.FirstOrDefault(c => c.Kind == ToolKind.YtDlp);
            if (ytDlpCheck?.UpdateAvailable == true && _orchestrator.ActiveJobCount == 0)
            {
                EngineStatusText = $"Updating yt-dlp → {ytDlpCheck.LatestVersion}...";
                EngineStatusColor = "#00A8E8";
                var updated = await _toolManager.UpdateToolAsync(ToolKind.YtDlp);
                EngineStatusText = $"Engine ready • yt-dlp {updated.InstalledVersion}";
                EngineStatusColor = "#2EA043";
                await Settings.RefreshToolStatusesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background tool update check skipped/failed.");
        }
    }

    [RelayCommand]
    private void DismissDatabaseRecoveryNotice() => DatabaseRecoveryNotice = null;

    [RelayCommand]
    private void SelectDownloads() => SelectedTab = AppNavigationTab.Downloads;

    [RelayCommand]
    private async Task SelectLibraryAsync()
    {
        SelectedTab = AppNavigationTab.Library;
        await Library.RefreshAsync();
    }

    [RelayCommand]
    private async Task SelectSettingsAsync()
    {
        SelectedTab = AppNavigationTab.Settings;
        await Settings.RefreshAllAsync();
    }

    [RelayCommand]
    private void RestartToApplyAppUpdate()
    {
        if (_orchestrator.ActiveJobCount > 0)
        {
            AppUpdateBannerText = "Finish or stop active downloads first";
            return;
        }

        _appUpdateService.ApplyUpdateAndRestart();
    }

    public Task ShutdownActiveDownloadsAsync() => _orchestrator.CancelAllAsync(TimeSpan.FromSeconds(12));
}
