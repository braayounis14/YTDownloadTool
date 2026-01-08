using System;
using System.Collections.Generic;
using System.Text;

namespace YTDownloadTool.Core
{
    public static class DownloadManager
    {
        public static DownloadQueue Queue { get; }

        static DownloadManager()
        {
            var settings = new DownloadSettings
            {
                UseCookiesFromChrome = true,
                ForceIPv6 = true,
                MaxParallelDownloads = 2
            };

            Queue = new DownloadQueue(settings);
        }
    }
}
