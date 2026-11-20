using CommunityToolkit.Mvvm.ComponentModel;
using System.Security.Policy;
using System.Text.RegularExpressions;
using YTDownloadTool.Core.Downloads;

namespace YTDownloadTool.ViewModels
{
    public partial class DownloadStatusViewModel : ObservableObject
    {
        [ObservableProperty] private string _title;
        [ObservableProperty] private string _metadata;
        [ObservableProperty] private string _thumbnail;
        [ObservableProperty] private double _progressWidth;

        // MISSING PROPERTIES ADDED HERE:
        [ObservableProperty] private string _status;
        [ObservableProperty] private string _speed;
        [ObservableProperty] private string _eta;

        public void Update(DownloadProgressInfo info)
        {
            Status = info.State; // info.State comes from the Record
            Speed = info.Speed;
            Eta = info.Eta;
            // Calculate 180px width based on percentage (matching your XAML design)
            ProgressWidth = (info.Percentage / 100.0) * 180.0;

            // Optional: Format a nice metadata string for the UI
            Metadata = $"{Speed} • {Eta} remaining";

            if (!string.IsNullOrEmpty(info.VideoId))
            {
                // maxresdefault is the highest quality available (1080p)
                // Fallback: If a video is old and doesn't have 1080p, 
                // you might need hqdefault.jpg as a secondary fallback.
                Thumbnail = $"https://i.ytimg.com/vi/{info.VideoId}/maxresdefault.jpg";

                if (Title == "Queued..." || string.IsNullOrEmpty(Title))
                {
                    Title = "Downloading Video...";
                }
            }
        }
    }
}