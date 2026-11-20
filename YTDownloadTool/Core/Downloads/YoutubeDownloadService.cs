using CliWrap;
using CliWrap.EventStream;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace YTDownloadTool.Core.Downloads
{
    public class YoutubeDownloadService : IDisposable
    {
        private readonly string _ytDlpPath;
        private readonly Channel<DownloadRequest> _downloadQueue;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly CancellationTokenSource _shutdownCts;
        private Task _processingTask;


        // REGEX to parse yt-dlp output: "[download]  45.0% of 10.00MiB at 2.50MiB/s ETA 00:05"
        private static readonly Regex ProgressRegex = new Regex(
            @"\[download\]\s+(?<percent>\d+(\.\d+)?)%.*at\s+(?<speed>[\d\.]+\w+/s).*ETA\s+(?<eta>[\d:]+)",
            RegexOptions.Compiled);

        public YoutubeDownloadService(string ytDlpPath)
        {
            _ytDlpPath = ytDlpPath;

            // Unbounded channel creates a high-performance queue
            _downloadQueue = Channel.CreateUnbounded<DownloadRequest>();

            // LIMIT CONCURRENCY: Only allow 1 or 2 simultaneous downloads to avoid IP bans
            _concurrencyLimiter = new SemaphoreSlim(1, 1);

            _shutdownCts = new CancellationTokenSource();

            // Start the consumer loop immediately
            _processingTask = Task.Run(ProcessQueueAsync);
        }

        // Public API to enqueue a download
        // Updated Method Signature to accept Quality and Extension
        public async Task EnqueueDownloadAsync(string url, string folderPath, string quality, string extension, IProgress<DownloadProgressInfo> progress)
        {
            // Construct the request using the constructor parameters
            var request = new DownloadRequest(
                url,
                Path.Combine(folderPath, "%(title)s.%(ext)s"),
                quality,
                extension,
                progress
            );

            await _downloadQueue.Writer.WriteAsync(request);
        }

        // The Consumer Loop
        private async Task ProcessQueueAsync()
        {
            try
            {
                await foreach (var request in _downloadQueue.Reader.ReadAllAsync(_shutdownCts.Token))
                {
                    // Wait for a slot (Rate limiting)
                    await _concurrencyLimiter.WaitAsync(_shutdownCts.Token);

                    try
                    {
                        await ExecuteDownloadAsync(request);
                    }
                    catch (Exception ex)
                    {
                        // Handle critical failure (log it)
                        request.ProgressReporter?.Report(new DownloadProgressInfo("", 0, "", "", $"Error: {ex.Message}"));
                    }
                    finally
                    {
                        // COOL DOWN: Wait 2 seconds before releasing the slot to protect IP
                        await Task.Delay(2000);
                        _concurrencyLimiter.Release();
                    }
                }
            }
            catch (OperationCanceledException) { /* Graceful shutdown */ }
        }

        // The CliWrap Execution Logic
        private async Task ExecuteDownloadAsync(DownloadRequest request)
        {
            // Map quality to height
            string height = request.Quality switch
            {
                "4k" => "2160",
                "2k" => "1440",
                _ => "1080"
            };


            var cmd = Cli.Wrap(_ytDlpPath)
                .WithArguments(args => args
                    .Add(request.VideoUrl)

                    // CLIENT STRATEGY
                    .Add("--extractor-args")
                    .Add("youtube:player-client=ios,tv,mweb,web")


                     // FORMAT (WINDOWS-SAFE)

                     //.Add("-f")
                     //.Add(
                     //    $"bestvideo[ext=mp4][height<=2160]\r\n/bestvideo[height<=2160]" +
                     //    $"bestaudio[ext=m4a]/bestaudio\r\n/best[height<=2160]/best"
                     //)

                     .Add("-f").Add("bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b")
                    // MERGE
                    .Add("--ffmpeg-location").Add(@"C:\Testing\ffmpeg.exe")
                    .Add("--merge-output-format").Add(request.Extension)

                    // OUTPUT
                    .Add("-o").Add(request.OutputPath)
                    .Add("--no-playlist")

                    // STABILITY
                    .Add("--retries").Add("10")
                    .Add("--fragment-retries").Add("10")
                    .Add("--retry-sleep").Add("1:5")
                    .Add("--concurrent-fragments").Add("2")

                    // NETWORK
                    .Add("--force-ipv4")
                    .Add("--socket-timeout").Add("15")

                    // UX
                    .Add("--progress")
                    .Add("--newline")
                // .Add("--no-warnings")
                );



            // Using ListenAsync to react to events in real-time
            await foreach (var cmdEvent in cmd.ListenAsync(_shutdownCts.Token))
            {
                switch (cmdEvent)
                {
                    case StandardOutputCommandEvent stdOut:
                        ParseProgress(stdOut.Text, request);
                        break;

                    case ExitedCommandEvent exited:
                        if (exited.ExitCode == 0)
                        {
                            request.ProgressReporter?.Report(
                                new DownloadProgressInfo(GetVideoId(request.VideoUrl), 100, "0B/s", "00:00", "Completed"));
                        }
                        else
                        {
                            request.ProgressReporter?.Report(
                               new DownloadProgressInfo(GetVideoId(request.VideoUrl), 0, "", "", "Failed"));
                        }
                        break;
                }
            }
        }
        private string GetVideoId(string url)
        {
            // Simple regex to pull the ID from standard or shortened YouTube URLs
            var regex = new Regex(@"(?:youtube\.com\/(?:[^\/]+\/.+\/|(?:v|e(?:mbed)?)\/|.*[?&]v=)|youtu\.be\/)([^""&?\/\s]{11})");
            var match = regex.Match(url);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        private void ParseProgress(string text, DownloadRequest request)
        {
            // Update UI that we are working
            if (string.IsNullOrWhiteSpace(text)) return;

            // Check if it's a progress line
            var match = ProgressRegex.Match(text);
            if (match.Success)
            {
                double.TryParse(match.Groups["percent"].Value, out double percent);
                string speed = match.Groups["speed"].Value;
                string eta = match.Groups["eta"].Value;

                request.ProgressReporter?.Report(
                    new DownloadProgressInfo(GetVideoId(request.VideoUrl), percent, speed, eta, "Downloading"));
            }
            // Check for processing (ffmpeg merges, etc.)
            else if (text.Contains("[Merger]") || text.Contains("[ExtractAudio]"))
            {
                request.ProgressReporter?.Report(
                    new DownloadProgressInfo(GetVideoId(request.VideoUrl), 100, "", "", "Processing..."));
            }
        }

        public void Dispose()
        {
            _shutdownCts.Cancel();
            _downloadQueue.Writer.Complete();
        }
    }
}
