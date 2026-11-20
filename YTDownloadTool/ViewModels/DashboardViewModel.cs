using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;


namespace YTDownloadTool.ViewModels
{

    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using System.Diagnostics;
    using System.IO;
    using System.Windows;

    using YTDownloadTool.Core.Downloads;

    public partial class DashboardViewModel : ObservableObject
    {
        private readonly YoutubeDownloadService _downloadService;
        private string _lastDownloadPath;

        [ObservableProperty] private string _urlInput;
        [ObservableProperty] private DownloadStatusViewModel _currentDownload;
        [ObservableProperty] private Visibility _statusCardVisibility = Visibility.Collapsed;

        // These would be bound to ComboBoxes in your XAML
        [ObservableProperty] private string _selectedQuality = "4k";
        [ObservableProperty] private string _selectedExtension = "mp4";

        // Change the constructor to this:
        public DashboardViewModel()
        {
            // Locate yt-dlp.exe in your app folder
            // string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Download-Worker.exe");
            string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Binaries", "Download-Worker.exe");

            _downloadService = new YoutubeDownloadService(exePath);

            _lastDownloadPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }


        [RelayCommand]
        private async Task StartDownload()
        {

            if (string.IsNullOrWhiteSpace(UrlInput)) return;

            StatusCardVisibility = Visibility.Visible;
            CurrentDownload = new DownloadStatusViewModel { Title = "Queued...", Status = "Waiting..." };

            var progressHandler = new Progress<DownloadProgressInfo>(info =>
            {
                CurrentDownload.Update(info);
            });

            await _downloadService.EnqueueDownloadAsync(
                UrlInput,
                _lastDownloadPath,
                SelectedQuality,
                SelectedExtension,
                progressHandler);

            UrlInput = string.Empty;
        }

        [RelayCommand]
        private void OpenFolder()
        {
            if (Directory.Exists(_lastDownloadPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastDownloadPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
    }

}