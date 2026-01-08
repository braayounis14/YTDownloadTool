using System.Windows.Input;
using YTDownloadTool.Core;
using YTDownloadTool.Helpers;

namespace YTDownloadTool.ViewModels
{
    public class DownloadItemViewModel : BaseViewModel
    {
        public DownloadJob Job { get; }
        private readonly DownloadQueue _queue;

        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand OpenPathCommand { get; }

        public DownloadItemViewModel(DownloadJob job, DownloadQueue queue)
        {
            Job = job;
            _queue = queue;

            PauseCommand = new RelayCommand(() => _queue.Pause(Job));
            ResumeCommand = new RelayCommand(async () => await _queue.EnqueueAsync(Job));
            OpenPathCommand = new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(Job.FilePath) && System.IO.File.Exists(Job.FilePath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Job.FilePath}\"");
            });
        }
    }

}
