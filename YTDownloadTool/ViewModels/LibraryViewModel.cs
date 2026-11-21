using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _removeCallback;
    private readonly Func<LibraryItemViewModel, Task> _verifyCallback;

    public LibraryItemViewModel(
        DownloadHistoryEntry entry,
        Func<string, Task> removeCallback,
        Func<LibraryItemViewModel, Task> verifyCallback)
    {
        Entry = entry;
        _removeCallback = removeCallback;
        _verifyCallback = verifyCallback;
        _integrityStatus = entry.IntegrityStatus;
        _integrityMessage = entry.IntegrityMessage;
        _resolution = entry.Resolution;
        _videoCodec = entry.VideoCodec;
        _audioCodec = entry.AudioCodec;
        _fileSizeBytes = entry.FileSizeBytes;
        _durationSeconds = entry.DurationSeconds;
        _verifiedAtUtc = entry.VerifiedAtUtc;
    }

    public DownloadHistoryEntry Entry { get; private set; }
    public string Id => Entry.Id;
    public string Title => Entry.Title;
    public string Channel => Entry.Channel;
    public string? ThumbnailUrl => Entry.ThumbnailUrl;
    public string QualityLabel => Entry.QualityLabel;
    public string Container => Entry.Container;
    public string CompletedAtText => Entry.CompletedAtUtc.ToLocalTime().ToString("g");
    public bool FileExists => !string.IsNullOrWhiteSpace(Entry.OutputFilePath) && File.Exists(Entry.OutputFilePath);
    public string BadgeText => Entry.WasLivestream ? "LIVESTREAM" : "VIDEO";

    [ObservableProperty] private MediaIntegrityStatus _integrityStatus;
    [ObservableProperty] private string? _integrityMessage;
    [ObservableProperty] private string? _resolution;
    [ObservableProperty] private string? _videoCodec;
    [ObservableProperty] private string? _audioCodec;
    [ObservableProperty] private long _fileSizeBytes;
    [ObservableProperty] private double? _durationSeconds;
    [ObservableProperty] private DateTimeOffset? _verifiedAtUtc;
    [ObservableProperty] private bool _isVerifying;

    public string FileSizeText => ShellHelper.FormatBytes(FileSizeBytes);
    public bool CanVerify => FileExists && !IsVerifying;
    public string VerifyButtonText => IsVerifying ? "Checking..." : "Verify / Repair";

    public string IntegrityBadgeText => IntegrityStatus switch
    {
        MediaIntegrityStatus.Verified => "VERIFIED",
        MediaIntegrityStatus.Repaired => "REPAIRED",
        MediaIntegrityStatus.NeedsAttention => "NEEDS ATTENTION",
        _ => "UNVERIFIED"
    };

    public string IntegrityBadgeColor => IntegrityStatus switch
    {
        MediaIntegrityStatus.Verified => "#2EA043",
        MediaIntegrityStatus.Repaired => "#00A8E8",
        MediaIntegrityStatus.NeedsAttention => "#D29922",
        _ => "#4E556B"
    };

    public string IntegrityTooltip
    {
        get
        {
            var baseMsg = string.IsNullOrWhiteSpace(IntegrityMessage)
                ? IntegrityStatus switch
                {
                    MediaIntegrityStatus.Verified => "Verified healthy via ffprobe.",
                    MediaIntegrityStatus.Repaired => "Container repaired losslessly and re-verified.",
                    MediaIntegrityStatus.NeedsAttention => "Container or stream issue detected. Click Verify / Repair to re-check.",
                    _ => "Not yet verified. Click Verify / Repair to inspect via ffprobe."
                }
                : IntegrityMessage;

            return VerifiedAtUtc.HasValue
                ? $"{baseMsg} (Checked {VerifiedAtUtc.Value.ToLocalTime():g})"
                : baseMsg;
        }
    }

    public string MediaSpecsSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Resolution))
                parts.Add(Resolution);

            var codecs = new List<string>();
            if (!string.IsNullOrWhiteSpace(VideoCodec))
                codecs.Add(VideoCodec.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(AudioCodec))
                codecs.Add(AudioCodec.ToUpperInvariant());
            if (codecs.Count > 0)
                parts.Add(string.Join("/", codecs));

            if (DurationSeconds is > 0)
                parts.Add(ShellHelper.FormatSeconds(DurationSeconds));

            return string.Join(" • ", parts);
        }
    }

    public void ApplyIntegrityResult(MediaIntegrityResult result)
    {
        IntegrityStatus = result.Status;
        IntegrityMessage = result.Message;
        Resolution = result.Resolution ?? Resolution;
        VideoCodec = result.VideoCodec ?? VideoCodec;
        AudioCodec = result.AudioCodec ?? AudioCodec;
        if (result.FileSizeBytes > 0)
            FileSizeBytes = result.FileSizeBytes;
        if (result.DurationSeconds is > 0)
            DurationSeconds = result.DurationSeconds;
        VerifiedAtUtc = result.VerifiedAtUtc;

        Entry = Entry with
        {
            IntegrityStatus = IntegrityStatus,
            IntegrityMessage = IntegrityMessage,
            Resolution = Resolution,
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            FileSizeBytes = FileSizeBytes,
            DurationSeconds = DurationSeconds,
            VerifiedAtUtc = VerifiedAtUtc
        };

        NotifyDerived();
    }

    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(VerifyButtonText));
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(FileExists));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(IntegrityBadgeText));
        OnPropertyChanged(nameof(IntegrityBadgeColor));
        OnPropertyChanged(nameof(IntegrityTooltip));
        OnPropertyChanged(nameof(MediaSpecsSummary));
    }

    [RelayCommand]
    private void Play()
    {
        if (FileExists)
            ShellHelper.OpenFileWithDefaultApp(Entry.OutputFilePath);
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        ShellHelper.RevealFileInExplorer(Entry.OutputFilePath);
    }

    [RelayCommand]
    private async Task VerifyOrRepairAsync()
    {
        if (!CanVerify) return;
        IsVerifying = true;
        try
        {
            await _verifyCallback(this);
        }
        finally
        {
            IsVerifying = false;
        }
    }

    [RelayCommand]
    private void CopyUrl()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Entry.SourceUrl))
                Clipboard.SetText(Entry.SourceUrl);
        }
        catch
        {
            // Ignore clipboard lock.
        }
    }

    [RelayCommand]
    private async Task RemoveFromHistoryAsync()
    {
        await _removeCallback(Entry.Id);
    }
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IHistoryStore _historyStore;
    private readonly IToolManager _toolManager;
    private readonly IMediaIntegrityService _integrityService;
    private readonly ILogger<LibraryViewModel> _logger;
    private List<DownloadHistoryEntry> _allEntries = [];

    public LibraryViewModel(
        IHistoryStore historyStore,
        IToolManager toolManager,
        IMediaIntegrityService integrityService,
        ILogger<LibraryViewModel> logger)
    {
        _historyStore = historyStore;
        _toolManager = toolManager;
        _integrityService = integrityService;
        _logger = logger;
    }

    public ObservableCollection<LibraryItemViewModel> FilteredItems { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string? _actionFeedback;

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _allEntries = (await _historyStore.LoadAsync()).ToList();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _historyStore.ClearAsync();
        _allEntries.Clear();
        ActionFeedback = null;
        ApplyFilter();
    }

    private async Task VerifyOrRepairEntryAsync(LibraryItemViewModel item)
    {
        ActionFeedback = null;
        try
        {
            var toolchain = await _toolManager.EnsureToolsReadyAsync();
            var result = await _integrityService.VerifyAndRepairIfNeededAsync(
                toolchain,
                item.Entry.OutputFilePath,
                expectVideo: null,
                expectAudio: true);

            await _historyStore.UpdateIntegrityAsync(
                item.Id,
                result.Status,
                result.Message,
                result.Resolution,
                result.VideoCodec,
                result.AudioCodec,
                result.FileSizeBytes > 0 ? result.FileSizeBytes : null,
                result.DurationSeconds,
                result.VerifiedAtUtc);

            item.ApplyIntegrityResult(result);

            var idx = _allEntries.FindIndex(e => string.Equals(e.Id, item.Id, StringComparison.Ordinal));
            if (idx >= 0)
                _allEntries[idx] = item.Entry;

            ActionFeedback = $"{item.Title}: {result.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual integrity verification failed for '{File}'.", item.Entry.OutputFilePath);
            ActionFeedback = $"Verification failed: {ex.Message}";
        }
    }

    private async Task RemoveEntryAsync(string id)
    {
        await _historyStore.RemoveAsync(id);
        _allEntries.RemoveAll(e => e.Id == id);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var q = SearchQuery?.Trim();

        var matches = string.IsNullOrWhiteSpace(q)
            ? _allEntries
            : _allEntries.Where(e =>
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Channel.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Container.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.IntegrityStatus.ToString().Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in matches)
        {
            FilteredItems.Add(new LibraryItemViewModel(entry, RemoveEntryAsync, VerifyOrRepairEntryAsync));
        }

        IsEmpty = FilteredItems.Count == 0;
    }
}
