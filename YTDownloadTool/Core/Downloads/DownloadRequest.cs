using System;
using System.Collections.Generic;
using System.Text;

namespace YTDownloadTool.Core.Downloads
{
    // Represents the request sent from the UI
    public record DownloadRequest(
        string VideoUrl,
        string OutputPath,
        string Quality, // "4k", "2k", "1080", etc.
        string Extension, // "mp4" or "mkv"
        IProgress<DownloadProgressInfo> ProgressReporter
    );
}
