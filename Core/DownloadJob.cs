using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YTDownloadTool.Core
{
    public enum DownloadState
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed
    }

    public class DownloadJob : INotifyPropertyChanged
    {
        public required string Url { get; init; }
        public required string FilePath { get; set; }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        private string _speed;
        public string Speed
        {
            get => _speed;
            set { _speed = value; OnPropertyChanged(); }
        }

        private string _eta;
        public string Eta
        {
            get => _eta;
            set { _eta = value; OnPropertyChanged(); }
        }

        private double _totalSizeMB;
        public double TotalSizeMB
        {
            get => _totalSizeMB;
            set { _totalSizeMB = value; OnPropertyChanged(); }
        }

        private double _downloadedMB;
        public double DownloadedMB
        {
            get => _downloadedMB;
            set { _downloadedMB = value; OnPropertyChanged(); }
        }

        private DownloadState _state;
        public DownloadState State
        {
            get => _state;
            set { _state = value; OnPropertyChanged(); }
        }

        public CancellationTokenSource CTS { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
