using System.Collections.ObjectModel;
using System.Windows.Input;
using YTDownloadTool.Core;
using YTDownloadTool.Helpers;

namespace YTDownloadTool.ViewModels
{
    public class DownloadsPageViewModel : BaseViewModel
    {
        private readonly DownloadQueue _queue;

        public ObservableCollection<DownloadItemViewModel> Downloads { get; }
            = new ObservableCollection<DownloadItemViewModel>();

        private string _videoUrl;
        public string VideoUrl
        {
            get => _videoUrl;
            set
            {
                _videoUrl = value;
                OnPropertyChanged();
            }
        }

        public ICommand DownloadCommand { get; }

        public DownloadsPageViewModel()
        {
            var settings = new DownloadSettings();
            _queue = new DownloadQueue(settings);

            DownloadCommand = new RelayCommand(async () =>
            {
                if (string.IsNullOrWhiteSpace(VideoUrl))
                    return;

                var job = new DownloadJob { Url = VideoUrl };
                var item = new DownloadItemViewModel(job, _queue);

                Downloads.Add(item);
                await _queue.EnqueueAsync(job);

                VideoUrl = string.Empty;
            });
        }
    }
}
