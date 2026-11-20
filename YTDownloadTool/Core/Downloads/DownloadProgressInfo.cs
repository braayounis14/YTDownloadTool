using System;
using System.Collections.Generic;
using System.Text;

namespace YTDownloadTool.Core.Downloads
{
    public record DownloadProgressInfo(
            string VideoId,
            double Percentage,
            string Speed,
            string Eta,
            string State // e.g., "Downloading", "Processing", "Done", "Error"
        );

}
