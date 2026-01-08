using System;
using System.Collections.Generic;
using System.Text;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace YTDownloadTool.Core
{
    public class DownloadService
    {
        private readonly YoutubeDL _ytdl;
        private readonly DownloadSettings _settings;

        public DownloadService(DownloadSettings settings)
        {
            _settings = settings;

            _ytdl = new YoutubeDL
            {
                YoutubeDLPath = @"C:\Testing\yt-dlp.exe",
                FFmpegPath = @"C:\Testing\ffmpeg-8.0.1-essentials_build\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe",
                OutputFolder = settings.OutputFolder
            };
        }

        public async Task StartAsync(DownloadJob job)
        {
            job.CTS = new CancellationTokenSource();
            job.State = DownloadState.Downloading;

            var progress = new Progress<DownloadProgress>(p =>
            {
                job.Progress = p.Progress;
                job.Speed = p.DownloadSpeed;
                job.Eta = p.ETA;
            });

            try
            {
                var res = await _ytdl.RunVideoDownload(
                    job.Url,
                    progress: progress,
                    ct: job.CTS.Token,
                    overrideOptions: _settings.ToOptionSet()
                );

                job.FilePath = res.Data;
                job.State = DownloadState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.State = DownloadState.Paused;
            }
            catch
            {
                job.State = DownloadState.Failed;
            }
        }

        public void Pause(DownloadJob job)
        {
            job.CTS?.Cancel();
        }
    }

}
