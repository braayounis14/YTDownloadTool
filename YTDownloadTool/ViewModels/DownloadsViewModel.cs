using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly IMediaProbeService _probeService;
    private readonly IDownloadOrchestrator _orchestrator;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<DownloadsViewModel> _logger;

    private CancellationTokenSource? _probeCts;

    public DownloadsViewModel(
        IMediaProbeService probeService,
        IDownloadOrchestrator orchestrator,
        ISettingsStore settingsStore,
        ILogger<DownloadsViewModel> logger)
    {
        _probeService = probeService;
        _orchestrator = orchestrator;
        _settingsStore = settingsStore;
        _logger = logger;

        var settings = _settingsStore.Current;
        OutputDirectory = settings.DownloadDirectory;
        SelectedLivestreamMode = settings.DefaultLivestreamMode;
        LiveEdgeMarginSeconds = settings.LiveEdgeMarginSeconds;
        SelectedContainer = settings.DefaultContainer;
    }

    public event EventHandler? DownloadCompleted;

    public ObservableCollection<QualityOption> AvailableQualities { get; } = [];
    public ObservableCollection<string> AvailableContainers { get; } = ["mp4", "mkv", "webm"];
    public ObservableCollection<DownloadJobItemViewModel> Jobs { get; } = [];
    public IReadOnlyList<int> LiveEdgeMarginPresets { get; } = [15, 30, 60, 120];

    [ObservableProperty] private string _urlInput = string.Empty;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string? _probeErrorMessage;
    [ObservableProperty] private MediaProbeResult? _probedMedia;
    [ObservableProperty] private QualityOption? _selectedQuality;
    [ObservableProperty] private string _selectedContainer = "mp4";
    [ObservableProperty] private string _outputDirectory = string.Empty;

    [ObservableProperty] private LivestreamRecordingMode _selectedLivestreamMode = LivestreamRecordingMode.RecordFromNow;
    [ObservableProperty] private int _liveEdgeMarginSeconds = 30;

    public bool HasProbedMedia => ProbedMedia is not null;
    public bool IsActiveLivestream => ProbedMedia?.IsActiveLivestream == true;
    public bool IsUpcomingLivestream => ProbedMedia?.IsUpcomingLivestream == true;
    public bool IsDvrAvailable => ProbedMedia?.DvrInfo?.IsDvrAvailable == true;
    public bool IsModeCSelected => IsActiveLivestream && SelectedLivestreamMode == LivestreamRecordingMode.RecordFromStartToLiveEdge;
    public bool HasJobs => Jobs.Count > 0;
    public bool CanStartDownload => ProbedMedia is not null && !ProbedMedia.IsUpcomingLivestream && SelectedQuality is not null && !IsProbing;

    public string ContentBadgeColor => ProbedMedia?.LiveStatus switch
    {
        MediaLiveStatus.IsLive => "#E91E63",
        MediaLiveStatus.IsUpcoming => "#D29922",
        MediaLiveStatus.WasLive => "#8957E5",
        MediaLiveStatus.PostLive => "#D29922",
        _ => "#00A8E8"
    };

    public string ProbedDurationText
    {
        get
        {
            if (ProbedMedia is null) return string.Empty;
            if (ProbedMedia.IsActiveLivestream)
            {
                var elapsed = ProbedMedia.DvrInfo?.StreamElapsed;
                return elapsed.HasValue
                    ? $"Live for {ShellHelper.FormatDuration(elapsed)}"
                    : "Broadcasting live";
            }

            if (ProbedMedia.IsUpcomingLivestream && ProbedMedia.ReleaseTimestampUtc.HasValue)
            {
                return $"Scheduled: {ProbedMedia.ReleaseTimestampUtc.Value.ToLocalTime():g}";
            }

            return $"Duration: {ShellHelper.FormatSeconds(ProbedMedia.DurationSeconds)}";
        }
    }

    public string? DvrBannerMessage => ProbedMedia?.DvrInfo?.WarningMessage;

    public string SelectedModeExplanation => SelectedLivestreamMode switch
    {
        LivestreamRecordingMode.RecordFromNow =>
            "Mode A: Starts recording from the current live moment and finishes automatically when the stream ends (or when you click Stop & Save).",
        LivestreamRecordingMode.RecordFromStartUntilEnd =>
            "Mode B (Experimental): Downloads from the earliest DVR segment YouTube exposes and continues recording until the livestream ends.",
        LivestreamRecordingMode.RecordFromStartToLiveEdge =>
            $"Mode C: Downloads historical segments from the beginning and automatically stops & merges ~{LiveEdgeMarginSeconds}s before the current live edge.",
        _ => string.Empty
    };

    public string StartButtonText => IsActiveLivestream ? "START RECORDING" : "DOWNLOAD";

    partial void OnProbedMediaChanged(MediaProbeResult? value)
    {
        OnPropertyChanged(nameof(HasProbedMedia));
        OnPropertyChanged(nameof(IsActiveLivestream));
        OnPropertyChanged(nameof(IsUpcomingLivestream));
        OnPropertyChanged(nameof(IsDvrAvailable));
        OnPropertyChanged(nameof(IsModeCSelected));
        OnPropertyChanged(nameof(ContentBadgeColor));
        OnPropertyChanged(nameof(ProbedDurationText));
        OnPropertyChanged(nameof(DvrBannerMessage));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(CanStartDownload));
    }

    partial void OnSelectedLivestreamModeChanged(LivestreamRecordingMode value)
    {
        OnPropertyChanged(nameof(IsModeCSelected));
        OnPropertyChanged(nameof(SelectedModeExplanation));

        if (ProbedMedia?.IsActiveLivestream == true)
        {
            RefreshQualitiesForCurrentMode();
        }
    }

    partial void OnLiveEdgeMarginSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedModeExplanation));
    }

    partial void OnSelectedQualityChanged(QualityOption? value)
    {
        OnPropertyChanged(nameof(CanStartDownload));
        if (value is null) return;

        AvailableContainers.Clear();
        foreach (var c in value.SupportedContainers)
            AvailableContainers.Add(c);

        if (!AvailableContainers.Contains(SelectedContainer, StringComparer.OrdinalIgnoreCase))
        {
            var preferred = _settingsStore.Current.DefaultContainer;
            SelectedContainer = AvailableContainers.FirstOrDefault(c => c.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                                ?? AvailableContainers.FirstOrDefault()
                                ?? "mp4";
        }
    }

    partial void OnIsProbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartDownload));
    }

    [RelayCommand]
    private void PasteUrl()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                UrlInput = Clipboard.GetText().Trim();
            }
        }
        catch
        {
            // Clipboard locked by another process.
        }
    }

    [RelayCommand]
    private async Task AnalyzeUrlAsync()
    {
        ProbeErrorMessage = null;

        var validation = YouTubeUrlValidator.Validate(UrlInput);
        if (!validation.IsValid)
        {
            ProbeErrorMessage = validation.ErrorMessage;
            return;
        }

        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        IsProbing = true;
        ProbedMedia = null;
        AvailableQualities.Clear();

        try
        {
            var result = await _probeService.ProbeAsync(validation.NormalizedUrl!, ct);
            ProbedMedia = result;

            // Default livestream mode safely based on DVR capability
            if (result.IsActiveLivestream)
            {
                var preferred = _settingsStore.Current.DefaultLivestreamMode;
                SelectedLivestreamMode = result.DvrInfo?.IsDvrAvailable == true
                    ? preferred
                    : LivestreamRecordingMode.RecordFromNow;
            }

            RefreshQualitiesForCurrentMode();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer analyze call.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "URL analysis failed for '{Input}'.", UrlInput);
            ProbeErrorMessage = ex.Message;
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var chosen = ShellHelper.BrowseForFolder(OutputDirectory, "Choose Download Destination");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            OutputDirectory = chosen;
        }
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (ProbedMedia is null || SelectedQuality is null)
            return;

        if (ProbedMedia.IsUpcomingLivestream)
        {
            ProbeErrorMessage = "This livestream is scheduled and hasn't started broadcasting yet.";
            return;
        }

        if (ProbedMedia.IsActiveLivestream
            && SelectedLivestreamMode != LivestreamRecordingMode.RecordFromNow
            && !IsDvrAvailable)
        {
            ProbeErrorMessage = "DVR is disabled for this stream. Select 'Record from now' (Mode A).";
            return;
        }

        var folder = string.IsNullOrWhiteSpace(OutputDirectory)
            ? _settingsStore.Current.DownloadDirectory
            : OutputDirectory;

        var jobRequest = new DownloadJobRequest(
            JobId: Guid.NewGuid().ToString("N"),
            VideoId: ProbedMedia.VideoId,
            SourceUrl: ProbedMedia.CanonicalUrl,
            Title: ProbedMedia.Title,
            Channel: ProbedMedia.Channel,
            ThumbnailUrl: ProbedMedia.ThumbnailUrl,
            OutputDirectory: folder,
            SelectedQuality: SelectedQuality,
            OutputContainer: SelectedContainer,
            LiveStatus: ProbedMedia.LiveStatus,
            LivestreamMode: ProbedMedia.IsActiveLivestream ? SelectedLivestreamMode : null,
            LiveEdgeMarginSeconds: Math.Clamp(LiveEdgeMarginSeconds, 15, 300),
            LivestreamStartUtc: ProbedMedia.ReleaseTimestampUtc,
            IsDvrAvailable: IsDvrAvailable,
            HasMuxedHlsFromStart: ProbedMedia.DvrInfo?.HasMuxedHlsFromStart == true,
            KnownDurationSeconds: ProbedMedia.DurationSeconds);

        await EnqueueRequestAsync(jobRequest);

        // Clear URL input ready for the next link while keeping active jobs visible
        UrlInput = string.Empty;
        ProbedMedia = null;
        AvailableQualities.Clear();
    }

    [RelayCommand]
    private void ClearFinishedJobs()
    {
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!Jobs[i].IsActive)
                Jobs.RemoveAt(i);
        }

        OnPropertyChanged(nameof(HasJobs));
    }

    public void RefreshSettingsFromStore()
    {
        var s = _settingsStore.Current;
        if (string.IsNullOrWhiteSpace(OutputDirectory))
            OutputDirectory = s.DownloadDirectory;
        LiveEdgeMarginSeconds = s.LiveEdgeMarginSeconds;
    }

    private async Task EnqueueRequestAsync(DownloadJobRequest request)
    {
        var itemVm = new DownloadJobItemViewModel(
            request,
            jobId => _orchestrator.RequestCancel(jobId),
            retryReq => EnqueueRequestAsync(retryReq with { JobId = Guid.NewGuid().ToString("N") }));

        Jobs.Insert(0, itemVm);
        OnPropertyChanged(nameof(HasJobs));

        var uiProgress = new Progress<DownloadProgressSnapshot>(snapshot =>
        {
            itemVm.ApplySnapshot(snapshot);
            if (snapshot.State == DownloadJobState.Completed)
            {
                DownloadCompleted?.Invoke(this, EventArgs.Empty);
            }
        });

        await _orchestrator.EnqueueAsync(request, uiProgress);
    }

    private void RefreshQualitiesForCurrentMode()
    {
        if (ProbedMedia is null)
            return;

        IReadOnlyList<QualityOption> source =
            ProbedMedia.IsActiveLivestream
            && SelectedLivestreamMode != LivestreamRecordingMode.RecordFromNow
            && ProbedMedia.DvrInfo?.FromStartQualities is { Count: > 0 } fromStart
                ? fromStart
                : ProbedMedia.AvailableQualities;

        AvailableQualities.Clear();
        foreach (var q in source)
            AvailableQualities.Add(q);

        var defaultQual = _settingsStore.Current.DefaultQuality;
        SelectedQuality = AvailableQualities.FirstOrDefault(q =>
                              !q.IsAudioOnly && q.Label.Contains(defaultQual, StringComparison.OrdinalIgnoreCase))
                          ?? AvailableQualities.FirstOrDefault();
    }
}
