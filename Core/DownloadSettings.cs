using System;
using System.Collections.Generic;
using System.Text;

namespace YTDownloadTool.Core
{
    // Core/DownloadSettings.cs
    using YoutubeDLSharp.Options;

    public class DownloadSettings
    {
        public string OutputFolder { get; set; } = @"C:\Users\Braa\Videos\NVIDIA";
        public int MaxParallelDownloads { get; set; } = 2;

        public long LimitRate { get; set; } = 2097152;
        public int SleepInterval { get; set; } = 5;
        public int MaxSleepInterval { get; set; } = 10;
        public int Retries { get; set; } = 10;
        public string RetrySleep { get; set; } = "5";
        public int ConcurrentFragments { get; set; } = 1;

        public bool UseCookiesFromChrome { get; set; } = false;
        public bool ForceIPv6 { get; set; } = true;

        public OptionSet ToOptionSet()
        {
            var options = new OptionSet
            {
                Continue = true,
                RestrictFilenames = true,

                LimitRate = LimitRate,
                SleepInterval = SleepInterval,
                MaxSleepInterval = MaxSleepInterval,
                Retries = Retries,
                RetrySleep = RetrySleep,
                ConcurrentFragments = ConcurrentFragments
            };

            if (UseCookiesFromChrome)
                options.CookiesFromBrowser = "chrome";

            if (ForceIPv6)
                options.SourceAddress = "::"; // IPv6 rotation

            return options;
        }
    }


}
