using System;
using System.Collections.Generic;
using System.Text;

namespace YTDownloadTool.ViewModels
{
    using CommunityToolkit.Mvvm.ComponentModel;
    using global::YTDownloadTool.Core;
    using System.Collections.ObjectModel;

    public class MainViewModel
    {
        public DownloadQueue Queue { get; }

        public ObservableCollection<DownloadJob> Jobs => Queue.Jobs;

        public MainViewModel()
        {
            var settings = new DownloadSettings
            {
                UseCookiesFromChrome = true,
                ForceIPv6 = true
            };

            Queue = new DownloadQueue(settings);
        }

        public async Task AddDownload(string url)
        {
            var job = new DownloadJob { Url = url };
            await Queue.EnqueueAsync(job);
        }




}
}
