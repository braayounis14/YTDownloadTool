using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed class DownloadJobCallbacks
{
    public required Action<string> Cancel { get; init; }
    public required Func<string, Task> Pause { get; init; }
    public required Func<string, Task> Resume { get; init; }
    public required Func<string, Task> RecoverPartial { get; init; }
    public required Func<string, Task> Discard { get; init; }
    public required Func<string, Task> MoveUp { get; init; }
    public required Func<string, Task> MoveDown { get; init; }
    public required Func<string, Task> MoveToTop { get; init; }
    public required Func<DownloadJobRequest, Task> Retry { get; init; }
}

public sealed partial class DownloadJobItemViewModel : ObservableObject
{
    private readonly DownloadJobCallbacks _callbacks;

    public DownloadJobItemViewModel(
        DownloadJobRequest request,
        DownloadJobCallbacks callbacks)
    {
        Request = request;
        _callbacks = callbacks;

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

    public static DownloadJobItemViewModel FromPersisted(PersistedDownloadJob persisted, DownloadJobCallbacks callbacks)
    {
        var req = persisted.ToDownloadJobRequest();
        var vm = new DownloadJobItemViewModel(req, callbacks)
        {
            State = persisted.Status,
            Percentage = persisted.ProgressPercent,
            IsIndeterminate = false,
            SizeText = persisted.TotalBytes is > 0
                ? $"{ShellHelper.FormatBytes(persisted.DownloadedBytes)} / {ShellHelper.FormatBytes(persisted.TotalBytes)}"
                : ShellHelper.FormatBytes(persisted.DownloadedBytes),
            OutputFilePath = persisted.OutputFilePath,
            ErrorMessage = persisted.Status is DownloadJobState.Failed ? persisted.LastErrorMessage : null,
            TechnicalDetails = persisted.LastTechnicalDetails,
            RetryAttempt = persisted.RetryAttempt,
            MaxRetryAttempts = persisted.MaxRetryAttempts,
            PhaseLabel = persisted.Status switch
            {
                DownloadJobState.Queued => "Queued...",
                DownloadJobState.Paused => string.IsNullOrWhiteSpace(persisted.LastErrorMessage)
                    ? "Paused — ready to resume"
                    : persisted.LastErrorMessage,
                DownloadJobState.InterruptedLivestream => string.IsNullOrWhiteSpace(persisted.LastErrorMessage)
                    ? "Livestream recording interrupted — partial media ready to recover."
                    : persisted.LastErrorMessage,
                _ => "Ready"
            }
        };

        vm.NotifyStateDerivedProperties();
        return vm;
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
    [ObservableProperty] private int _retryAttempt;
    [ObservableProperty] private int _maxRetryAttempts;
    [ObservableProperty] private bool _isRecovering;

    public bool IsQueued => State == DownloadJobState.Queued;
    public bool IsPaused => State == DownloadJobState.Paused;
    public bool IsRetrying => State == DownloadJobState.Retrying;
    public bool IsInterruptedLivestream => State == DownloadJobState.InterruptedLivestream;

    public bool IsActive => State is DownloadJobState.Queued
        or DownloadJobState.Preparing
        or DownloadJobState.Downloading
        or DownloadJobState.CatchingUp
        or DownloadJobState.RecordingLive
        or DownloadJobState.Finalizing
        or DownloadJobState.Retrying;

    /// <summary>Pause is strictly VOD-only (disabled on active livestreams, which preserve Stop &amp; Save).</summary>
    public bool CanPause => !IsLivestream && State is DownloadJobState.Queued
        or DownloadJobState.Preparing
        or DownloadJobState.Downloading
        or DownloadJobState.Retrying;

    public bool CanResume => !IsLivestream && IsPaused;
    public bool CanMoveInQueue => IsQueued;
    public bool CanRecoverPartial => IsInterruptedLivestream && !IsRecovering;
    public bool CanDiscard => IsPaused || IsInterruptedLivestream;
    public bool CanClearFromList => State is DownloadJobState.Completed or DownloadJobState.Cancelled or DownloadJobState.Failed;

    public bool IsCompleted => State == DownloadJobState.Completed;
    public bool IsFailed => State == DownloadJobState.Failed;
    public bool IsCancelled => State == DownloadJobState.Cancelled;
    public bool HasFileOutput => !string.IsNullOrWhiteSpace(OutputFilePath) && System.IO.File.Exists(OutputFilePath);

    public string CancelButtonText => IsLivestream ? "STOP & SAVE" : "CANCEL";
    public string PhaseLabelColor => IsRetrying ? "#F5D076" : (IsInterruptedLivestream ? "#FF7CA3" : "#D8DCEB");

    public string StatusBadgeColor => State switch
    {
        DownloadJobState.Completed => "#2EA043",
        DownloadJobState.Failed => "#DA3633",
        DownloadJobState.Cancelled => "#6E7681",
        DownloadJobState.Paused => "#D29922",
        DownloadJobState.Retrying => "#F0883E",
        DownloadJobState.InterruptedLivestream => "#E91E63",
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
        DownloadJobState.Paused => "PAUSED",
        DownloadJobState.Retrying => MaxRetryAttempts > 0 ? $"RETRY {RetryAttempt}/{MaxRetryAttempts}" : "RETRYING",
        DownloadJobState.InterruptedLivestream => "INTERRUPTED LIVE",
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
        RetryAttempt = snapshot.RetryAttempt;
        MaxRetryAttempts = snapshot.MaxRetryAttempts;

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

        NotifyStateDerivedProperties();
    }

    private void NotifyStateDerivedProperties()
    {
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsRetrying));
        OnPropertyChanged(nameof(IsInterruptedLivestream));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanMoveInQueue));
        OnPropertyChanged(nameof(CanRecoverPartial));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(CanClearFromList));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(HasFileOutput));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(PhaseLabelColor));
    }

    [RelayCommand]
    private void CancelOrStop()
    {
        if (IsActive)
        {
            PhaseLabel = IsLivestream ? "Stopping & finalizing recording..." : "Cancelling...";
            _callbacks.Cancel(JobId);
        }
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        if (!CanPause) return;
        PhaseLabel = "Pausing (keeping partial files)...";
        await _callbacks.Pause(JobId);
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (!CanResume) return;
        await _callbacks.Resume(JobId);
    }

    [RelayCommand]
    private async Task RecoverPartialAsync()
    {
        if (!CanRecoverPartial) return;
        IsRecovering = true;
        OnPropertyChanged(nameof(CanRecoverPartial));
        try
        {
            await _callbacks.RecoverPartial(JobId);
        }
        finally
        {
            IsRecovering = false;
            OnPropertyChanged(nameof(CanRecoverPartial));
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (!CanDiscard) return;
        await _callbacks.Discard(JobId);
    }

    [RelayCommand]
    private async Task MoveUpAsync()
    {
        if (CanMoveInQueue)
            await _callbacks.MoveUp(JobId);
    }

    [RelayCommand]
    private async Task MoveDownAsync()
    {
        if (CanMoveInQueue)
            await _callbacks.MoveDown(JobId);
    }

    [RelayCommand]
    private async Task MoveToTopAsync()
    {
        if (CanMoveInQueue)
            await _callbacks.MoveToTop(JobId);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _callbacks.Retry(Request);
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
