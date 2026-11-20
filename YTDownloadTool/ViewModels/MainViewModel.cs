using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
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
    private readonly IDownloadOrchestrator _orchestrator;
    private readonly IAppUpdateService _appUpdateService;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        DownloadsViewModel downloadsViewModel,
        LibraryViewModel libraryViewModel,
        SettingsViewModel settingsViewModel,
        IToolManager toolManager,
        ISettingsStore settingsStore,
        IDownloadOrchestrator orchestrator,
        IAppUpdateService appUpdateService,
        ILogger<MainViewModel> logger)
    {
        Downloads = downloadsViewModel;
        Library = libraryViewModel;
        Settings = settingsViewModel;
        _toolManager = toolManager;
        _settingsStore = settingsStore;
        _orchestrator = orchestrator;
        _appUpdateService = appUpdateService;
        _logger = logger;

        AppVersionLabel = $"v{_appUpdateService.CurrentVersion}";

        Downloads.DownloadCompleted += async (_, _) => await Library.RefreshAsync();
        Settings.SettingsSaved += (_, _) => Downloads.RefreshSettingsFromStore();
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
        Downloads.RefreshSettingsFromStore();
        await Library.RefreshAsync();

        // 1. Ensure multimedia toolchain (yt-dlp, FFmpeg, Deno) is healthy / seeded / ready
        var statusProgress = new Progress<string>(msg => EngineStatusText = msg);
        try
        {
            var toolchain = await _toolManager.EnsureToolsReadyAsync(statusProgress);
            EngineStatusText = $"Engine ready • yt-dlp {toolchain.YtDlpVersion}";
            EngineStatusColor = "#2EA043";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial multimedia engine readiness check failed.");
            EngineStatusText = "Engine needs repair (open Settings)";
            EngineStatusColor = "#DA3633";
        }

        await Settings.RefreshToolStatusesAsync();

        // 2. Background independent tool update check (respects per-tool cadences: yt-dlp 24h, FFmpeg/Deno 30d)
        if (_settingsStore.Current.AutoUpdateTools)
        {
            _ = RunBackgroundToolUpdatesAsync();
        }

        // 3. Background rate-limited Velopack app update check (throttled >= 12h, ignores HTTP 403 silently)
        _ = _appUpdateService.CheckAndDownloadIfAvailableAsync(isManualCheck: false);
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
        await Settings.RefreshToolStatusesAsync();
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
