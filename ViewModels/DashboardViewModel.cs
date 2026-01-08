using System.Collections.ObjectModel;
using System.Windows.Input;
using YTDownloadTool.Core;
using YTDownloadTool.Helpers;

namespace YTDownloadTool.ViewModels
{

    public class DashboardPageViewModel : BaseViewModel
    {
        public ObservableCollection<DownloadItemViewModel> Downloads { get; }
        private readonly DownloadQueue _queue;

        public string VideoUrl { get; set; }

        public ICommand DownloadCommand { get; }

        public DashboardPageViewModel()
        {
            var settings = new DownloadSettings();
            _queue = new DownloadQueue(settings);
            Downloads = new ObservableCollection<DownloadItemViewModel>();

            DownloadCommand = new RelayCommand(async () =>
            {
                if (string.IsNullOrWhiteSpace(VideoUrl)) return;

                var job = new DownloadJob { Url = VideoUrl };
                var itemVM = new DownloadItemViewModel(job, _queue);
                Downloads.Add(itemVM);

                await _queue.EnqueueAsync(job);
            });
        }
    }

}