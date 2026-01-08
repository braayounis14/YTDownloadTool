using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace YTDownloadTool.Core
{
    public class DownloadQueue
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly DownloadService _service;

        public ObservableCollection<DownloadJob> Jobs { get; } = new();

        public DownloadQueue(DownloadSettings settings)
        {
            _semaphore = new SemaphoreSlim(settings.MaxParallelDownloads);
            _service = new DownloadService(settings);
        }

        public async Task EnqueueAsync(DownloadJob job)
        {
            Jobs.Add(job);
            job.State = DownloadState.Queued;

            await _semaphore.WaitAsync();

            try
            {
                await _service.StartAsync(job);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Pause(DownloadJob job)
        {
            _service.Pause(job);
        }
    }


}
