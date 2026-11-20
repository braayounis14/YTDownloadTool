using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed partial class ToolItemViewModel : ObservableObject
{
    private readonly Func<ToolKind, IProgress<double>, Task> _updateAction;
    private readonly Func<ToolKind, Task> _rollbackAction;
    private readonly Func<ToolKind, IProgress<double>, Task> _repairAction;

    public ToolItemViewModel(
        ToolStatusInfo status,
        Func<ToolKind, IProgress<double>, Task> updateAction,
        Func<ToolKind, Task> rollbackAction,
        Func<ToolKind, IProgress<double>, Task> repairAction)
    {
        Kind = status.Kind;
        _updateAction = updateAction;
        _rollbackAction = rollbackAction;
        _repairAction = repairAction;
        Apply(status);
    }

    public ToolKind Kind { get; }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private ToolHealthState _state;
    [ObservableProperty] private string _installedVersion = "Not installed";
    [ObservableProperty] private string? _previousVersion;
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _canRollback;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _downloadProgress;

    public string StateBadgeColor => State switch
    {
        ToolHealthState.Ready => "#2EA043",
        ToolHealthState.Outdated => "#D29922",
        ToolHealthState.Updating => "#00A8E8",
        _ => "#DA3633"
    };

    public void Apply(ToolStatusInfo info)
    {
        DisplayName = info.DisplayName;
        State = info.State;
        InstalledVersion = string.IsNullOrWhiteSpace(info.InstalledVersion) ? "Not installed" : info.InstalledVersion;
        PreviousVersion = info.PreviousVersion;
        AvailableVersion = info.AvailableVersion;
        UpdateAvailable = info.UpdateAvailable;
        CanRollback = info.CanRollback;
        StatusMessage = info.Message;
        OnPropertyChanged(nameof(StateBadgeColor));
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        DownloadProgress = 0;
        StatusMessage = "Downloading & verifying update...";
        try
        {
            await _updateAction(Kind, new Progress<double>(p => DownloadProgress = p));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (IsBusy || !CanRollback) return;
        IsBusy = true;
        StatusMessage = "Rolling back...";
        try
        {
            await _rollbackAction(Kind);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        DownloadProgress = 0;
        StatusMessage = "Repairing tool...";
        try
        {
            await _repairAction(Kind, new Progress<double>(p => DownloadProgress = p));
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IToolManager _toolManager;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        IToolManager toolManager,
        IAppUpdateService appUpdateService,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settingsStore = settingsStore;
        _toolManager = toolManager;
        _appUpdateService = appUpdateService;
        _paths = paths;
        _logger = logger;

        AppVersionText = $"YTDownloadTool v{_appUpdateService.CurrentVersion}";
        AppUpdateStatusText = _appUpdateService.CurrentState.StatusMessage;
        IsAppUpdateReadyToRestart = _appUpdateService.CurrentState.IsDownloadedReadyToRestart;
        _appUpdateService.StateChanged += (_, s) =>
        {
            AppUpdateStatusText = s.StatusMessage;
            IsAppUpdateReadyToRestart = s.IsDownloadedReadyToRestart;
        };

        ThirdPartyNoticesText = LoadNoticesText();
        LoadFieldsFromSettings(_settingsStore.Current);
    }

    public event EventHandler? SettingsSaved;

    public ObservableCollection<ToolItemViewModel> Tools { get; } = [];
    public IReadOnlyList<string> QualityOptions { get; } = ["2160p", "1440p", "1080p", "720p", "480p"];
    public IReadOnlyList<string> ContainerOptions { get; } = ["mp4", "mkv", "webm"];
    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];
    public IReadOnlyList<int> LiveMarginOptions { get; } = [15, 30, 60, 120];
    public IReadOnlyList<string> YtDlpChannelOptions { get; } = ["stable", "nightly"];

    public string AppVersionText { get; }
    public string ThirdPartyNoticesText { get; }

    [ObservableProperty] private string _downloadDirectory = string.Empty;
    [ObservableProperty] private string _defaultQuality = "1080p";
    [ObservableProperty] private string _defaultContainer = "mp4";
    [ObservableProperty] private int _maxConcurrentDownloads = 2;
    [ObservableProperty] private LivestreamRecordingMode _defaultLivestreamMode = LivestreamRecordingMode.RecordFromNow;
    [ObservableProperty] private int _liveEdgeMarginSeconds = 30;
    [ObservableProperty] private string _ytDlpChannel = "stable";
    [ObservableProperty] private bool _autoUpdateTools = true;
    [ObservableProperty] private bool _autoCheckAppUpdates = true;

    [ObservableProperty] private string? _saveFeedback;
    [ObservableProperty] private string? _engineActionFeedback;
    [ObservableProperty] private bool _isCheckingEngineUpdates;
    [ObservableProperty] private bool _isCheckingAppUpdates;
    [ObservableProperty] private string _appUpdateStatusText = string.Empty;
    [ObservableProperty] private bool _isAppUpdateReadyToRestart;
    [ObservableProperty] private bool _showNotices;

    public async Task RefreshToolStatusesAsync()
    {
        var statuses = await _toolManager.GetStatusesAsync();
        Tools.Clear();
        foreach (var status in statuses)
        {
            Tools.Add(new ToolItemViewModel(
                status,
                UpdateSingleToolAsync,
                RollbackSingleToolAsync,
                RepairSingleToolAsync));
        }
    }

    [RelayCommand]
    private void BrowseDefaultFolder()
    {
        var chosen = ShellHelper.BrowseForFolder(DownloadDirectory, "Select Default Download Folder");
        if (!string.IsNullOrWhiteSpace(chosen))
            DownloadDirectory = chosen;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var current = _settingsStore.Current;
        current.DownloadDirectory = DownloadDirectory;
        current.DefaultQuality = DefaultQuality;
        current.DefaultContainer = DefaultContainer;
        current.MaxConcurrentDownloads = MaxConcurrentDownloads;
        current.DefaultLivestreamMode = DefaultLivestreamMode;
        current.LiveEdgeMarginSeconds = LiveEdgeMarginSeconds;
        current.YtDlpChannel = YtDlpChannel;
        current.AutoUpdateTools = AutoUpdateTools;
        current.AutoCheckAppUpdates = AutoCheckAppUpdates;

        await _settingsStore.SaveAsync(current);
        SaveFeedback = "Settings saved.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task CheckEngineUpdatesAsync()
    {
        if (IsCheckingEngineUpdates) return;
        IsCheckingEngineUpdates = true;
        EngineActionFeedback = "Checking official GitHub releases for yt-dlp, FFmpeg, and Deno...";
        try
        {
            var results = await _toolManager.CheckAllForUpdatesAsync(force: true);
            await RefreshToolStatusesAsync();
            var updatesCount = results.Count(r => r.UpdateAvailable);
            EngineActionFeedback = updatesCount > 0
                ? $"{updatesCount} engine update(s) available."
                : "All multimedia tools are up to date.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual engine update check failed.");
            EngineActionFeedback = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingEngineUpdates = false;
        }
    }

    [RelayCommand]
    private async Task CheckAppUpdatesAsync()
    {
        if (IsCheckingAppUpdates) return;
        IsCheckingAppUpdates = true;
        AppUpdateStatusText = "Checking for application updates...";
        try
        {
            var state = await _appUpdateService.CheckAndDownloadIfAvailableAsync(isManualCheck: true);
            AppUpdateStatusText = state.StatusMessage;
            IsAppUpdateReadyToRestart = state.IsDownloadedReadyToRestart;
        }
        finally
        {
            IsCheckingAppUpdates = false;
        }
    }

    [RelayCommand]
    private void ApplyAppUpdateNow()
    {
        _appUpdateService.ApplyUpdateAndRestart();
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        _paths.EnsureDirectoriesExist();
        ShellHelper.OpenFolder(_paths.LogsDirectory);
    }

    [RelayCommand]
    private void ToggleNotices()
    {
        ShowNotices = !ShowNotices;
    }

    private async Task UpdateSingleToolAsync(ToolKind kind, IProgress<double> progress)
    {
        try
        {
            var updated = await _toolManager.UpdateToolAsync(kind, progress);
            ApplySingleToolStatus(updated);
            EngineActionFeedback = $"{updated.DisplayName} updated to {updated.InstalledVersion}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update {Tool}.", kind);
            EngineActionFeedback = $"Failed to update {kind}: {ex.Message}";
            await RefreshToolStatusesAsync();
        }
    }

    private async Task RollbackSingleToolAsync(ToolKind kind)
    {
        try
        {
            var rolled = await _toolManager.RollbackToolAsync(kind);
            ApplySingleToolStatus(rolled);
            EngineActionFeedback = $"Rolled back {rolled.DisplayName} to {rolled.InstalledVersion}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to roll back {Tool}.", kind);
            EngineActionFeedback = $"Rollback failed for {kind}: {ex.Message}";
        }
    }

    private async Task RepairSingleToolAsync(ToolKind kind, IProgress<double> progress)
    {
        try
        {
            var repaired = await _toolManager.RepairToolAsync(kind, progress);
            ApplySingleToolStatus(repaired);
            EngineActionFeedback = $"{repaired.DisplayName} repaired ({repaired.InstalledVersion}).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to repair {Tool}.", kind);
            EngineActionFeedback = $"Repair failed for {kind}: {ex.Message}";
        }
    }

    private void ApplySingleToolStatus(ToolStatusInfo info)
    {
        var vm = Tools.FirstOrDefault(t => t.Kind == info.Kind);
        vm?.Apply(info);
    }

    private void LoadFieldsFromSettings(AppSettings s)
    {
        DownloadDirectory = s.DownloadDirectory;
        DefaultQuality = s.DefaultQuality;
        DefaultContainer = s.DefaultContainer;
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        DefaultLivestreamMode = s.DefaultLivestreamMode;
        LiveEdgeMarginSeconds = s.LiveEdgeMarginSeconds;
        YtDlpChannel = s.YtDlpChannel;
        AutoUpdateTools = s.AutoUpdateTools;
        AutoCheckAppUpdates = s.AutoCheckAppUpdates;
    }

    private string LoadNoticesText()
    {
        var path = Path.Combine(_paths.AppDirectory, "THIRD_PARTY_NOTICES.txt");
        if (File.Exists(path))
        {
            try { return File.ReadAllText(path); } catch { }
        }

        return "YTDownloadTool uses yt-dlp (The Unlicense), FFmpeg (GPLv3 via yt-dlp/FFmpeg-Builds), Deno (MIT), and Velopack (MIT).";
    }
}
