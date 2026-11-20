using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed partial class DownloadJobItemViewModel : ObservableObject
{
    private readonly Action<string> _cancelCallback;
    private readonly Func<DownloadJobRequest, Task> _retryCallback;

    public DownloadJobItemViewModel(
        DownloadJobRequest request,
        Action<string> cancelCallback,
        Func<DownloadJobRequest, Task> retryCallback)
    {
        Request = request;
        _cancelCallback = cancelCallback;
        _retryCallback = retryCallback;

        JobId = request.JobId;
        Title = request.Title;
        Channel = request.Channel;
        ThumbnailUrl = request.ThumbnailUrl;
        QualityLabel = request.SelectedQuality.Label;
        ContainerBadge = (request.SelectedQuality.IsAudioOnly
            ? request.SelectedQuality.PreferredAudioExtension ?? request.OutputContainer
            : request.OutputContainer).ToUpperInvariant();
        IsLivestream = request.LiveStatus == MediaLiveStatus.IsLive;
        LivestreamModeBadge = request.LivestreamMode switch
        {
            LivestreamRecordingMode.RecordFromNow => "LIVE • Mode A (From Now)",
            LivestreamRecordingMode.RecordFromStartUntilEnd => "LIVE • Mode B (From Start)",
            LivestreamRecordingMode.RecordFromStartToLiveEdge => $"LIVE • Mode C (Stop -{request.LiveEdgeMarginSeconds}s)",
            _ => request.LiveStatus == MediaLiveStatus.WasLive ? "COMPLETED LIVE" : "VIDEO"
        };
        OutputDirectory = request.OutputDirectory;
    }

    public DownloadJobRequest Request { get; }
    public string JobId { get; }
    public string Title { get; }
    public string Channel { get; }
    public string? ThumbnailUrl { get; }
    public string QualityLabel { get; }
    public string ContainerBadge { get; }
    public bool IsLivestream { get; }
    public string LivestreamModeBadge { get; }
    public string OutputDirectory { get; }

    [ObservableProperty] private DownloadJobState _state = DownloadJobState.Queued;
    [ObservableProperty] private double _percentage;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _phaseLabel = "Queued...";
    [ObservableProperty] private string _speedText = "--";
    [ObservableProperty] private string _sizeText = "--";
    [ObservableProperty] private string _etaOrDurationText = "--:--";
    [ObservableProperty] private string _etaHeaderText = "ETA";
    [ObservableProperty] private string? _outputFilePath;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _technicalDetails;
    [ObservableProperty] private string? _nonFatalNotice;
    [ObservableProperty] private bool _showDiagnostics;

    public bool IsActive => State is DownloadJobState.Queued or DownloadJobState.Preparing or DownloadJobState.Downloading
        or DownloadJobState.CatchingUp or DownloadJobState.RecordingLive or DownloadJobState.Finalizing;

    public bool IsCompleted => State == DownloadJobState.Completed;
    public bool IsFailed => State == DownloadJobState.Failed;
    public bool IsCancelled => State == DownloadJobState.Cancelled;
    public bool HasFileOutput => !string.IsNullOrWhiteSpace(OutputFilePath) && System.IO.File.Exists(OutputFilePath);

    public string CancelButtonText => IsLivestream ? "STOP & SAVE" : "CANCEL";

    public string StatusBadgeColor => State switch
    {
        DownloadJobState.Completed => "#2EA043",
        DownloadJobState.Failed => "#DA3633",
        DownloadJobState.Cancelled => "#6E7681",
        DownloadJobState.RecordingLive => "#E91E63",
        DownloadJobState.CatchingUp => "#F0883E",
        DownloadJobState.Finalizing => "#A371F7",
        _ => "#00A8E8"
    };

    public string StateLabel => State switch
    {
        DownloadJobState.Queued => "QUEUED",
        DownloadJobState.Preparing => "PREPARING",
        DownloadJobState.Downloading => $"{Percentage:F0}%",
        DownloadJobState.CatchingUp => Percentage > 0 ? $"CATCH-UP {Percentage:F0}%" : "CATCHING UP",
        DownloadJobState.RecordingLive => "RECORDING LIVE",
        DownloadJobState.Finalizing => "FINALIZING",
        DownloadJobState.Completed => "COMPLETED",
        DownloadJobState.Cancelled => "CANCELLED",
        DownloadJobState.Failed => "FAILED",
        _ => "WORKING"
    };

    public void ApplySnapshot(DownloadProgressSnapshot snapshot)
    {
        State = snapshot.State;
        Percentage = snapshot.Percentage;
        IsIndeterminate = snapshot.IsIndeterminate;
        PhaseLabel = snapshot.PhaseLabel;
        SpeedText = ShellHelper.FormatSpeed(snapshot.SpeedBytesPerSecond);

        SizeText = snapshot.TotalBytes is > 0
            ? $"{ShellHelper.FormatBytes(snapshot.DownloadedBytes)} / {ShellHelper.FormatBytes(snapshot.TotalBytes)}"
            : ShellHelper.FormatBytes(snapshot.DownloadedBytes);

        if (IsLivestream && snapshot.RecordedDuration.HasValue)
        {
            EtaHeaderText = "CAPTURED";
            EtaOrDurationText = ShellHelper.FormatDuration(snapshot.RecordedDuration);
        }
        else
        {
            EtaHeaderText = "ETA";
            EtaOrDurationText = ShellHelper.FormatDuration(snapshot.Eta);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OutputFilePath))
            OutputFilePath = snapshot.OutputFilePath;

        ErrorMessage = snapshot.ErrorMessage;
        TechnicalDetails = snapshot.TechnicalDetails;
        if (!string.IsNullOrWhiteSpace(snapshot.NonFatalNotice))
            NonFatalNotice = snapshot.NonFatalNotice;

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(HasFileOutput));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(StateLabel));
    }

    [RelayCommand]
    private void CancelOrStop()
    {
        if (IsActive)
        {
            PhaseLabel = IsLivestream ? "Stopping & finalizing recording..." : "Cancelling...";
            _cancelCallback(JobId);
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _retryCallback(Request);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (HasFileOutput)
            ShellHelper.RevealFileInExplorer(OutputFilePath);
        else
            ShellHelper.OpenFolder(OutputDirectory);
    }

    [RelayCommand]
    private void PlayFile()
    {
        if (HasFileOutput)
            ShellHelper.OpenFileWithDefaultApp(OutputFilePath);
    }

    [RelayCommand]
    private void ToggleDiagnostics()
    {
        ShowDiagnostics = !ShowDiagnostics;
    }
}
