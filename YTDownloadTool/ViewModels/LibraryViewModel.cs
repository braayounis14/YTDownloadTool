using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Services;

namespace YTDownloadTool.ViewModels;

public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _removeCallback;

    public LibraryItemViewModel(DownloadHistoryEntry entry, Func<string, Task> removeCallback)
    {
        Entry = entry;
        _removeCallback = removeCallback;
    }

    public DownloadHistoryEntry Entry { get; }
    public string Id => Entry.Id;
    public string Title => Entry.Title;
    public string Channel => Entry.Channel;
    public string? ThumbnailUrl => Entry.ThumbnailUrl;
    public string QualityLabel => Entry.QualityLabel;
    public string Container => Entry.Container;
    public string FileSizeText => ShellHelper.FormatBytes(Entry.FileSizeBytes);
    public string CompletedAtText => Entry.CompletedAtUtc.ToLocalTime().ToString("g");
    public bool FileExists => !string.IsNullOrWhiteSpace(Entry.OutputFilePath) && File.Exists(Entry.OutputFilePath);
    public string BadgeText => Entry.WasLivestream ? "LIVESTREAM" : "VIDEO";

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
    private List<DownloadHistoryEntry> _allEntries = [];

    public LibraryViewModel(IHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    public ObservableCollection<LibraryItemViewModel> FilteredItems { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;

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
        ApplyFilter();
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
                || e.Container.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in matches)
        {
            FilteredItems.Add(new LibraryItemViewModel(entry, RemoveEntryAsync));
        }

        IsEmpty = FilteredItems.Count == 0;
    }
}
