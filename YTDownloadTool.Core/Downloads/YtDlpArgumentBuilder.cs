using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public static class YtDlpArgumentBuilder
{
    public static IReadOnlyList<string> BuildStandardDownloadArguments(
        ResolvedToolchain toolchain,
        DownloadJobRequest request,
        string reservedBaseOutputPath)
    {
        var args = new List<string>();
        AddCommonEngineArguments(args, toolchain);

        if (request.SelectedQuality.IsAudioOnly)
        {
            var audioExt = (request.SelectedQuality.PreferredAudioExtension ?? request.OutputContainer).ToLowerInvariant();
            args.Add("-f");
            args.Add(request.SelectedQuality.FormatSelector);
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(audioExt);
            args.Add("--audio-quality");
            args.Add("0");
        }
        else
        {
            var container = request.OutputContainer.ToLowerInvariant();
            args.Add("-f");
            args.Add(BuildContainerAwareFormatSelector(request.SelectedQuality, container));

            args.Add("--merge-output-format");
            args.Add(container);

            // Remux cleanly (stream copy, no re-encoding) if a single stream downloaded in another container
            if (container is "mp4" or "mkv")
            {
                args.Add("--remux-video");
                args.Add(container);
            }
        }

        // Deterministic output path based on our OutputPathGuard reservation
        args.Add("-o");
        args.Add($"{reservedBaseOutputPath}.%(ext)s");
        args.Add("--windows-filenames");
        args.Add("--no-playlist");

        // Network stability & retry policy
        args.Add("--retries");
        args.Add("10");
        args.Add("--fragment-retries");
        args.Add("10");
        args.Add("--retry-sleep");
        args.Add("http:exp=1:10");
        args.Add("--retry-sleep");
        args.Add("fragment:exp=1:10");
        args.Add("--concurrent-fragments");
        args.Add("4");
        args.Add("--socket-timeout");
        args.Add("20");

        AddProgressAndPrintArguments(args);

        args.Add("--");
        args.Add(request.SourceUrl);
        return args;
    }

    public static IReadOnlyList<string> BuildLivestreamArguments(
        ResolvedToolchain toolchain,
        DownloadJobRequest request,
        string reservedBaseOutputPath,
        LivestreamRecordingMode mode)
    {
        var args = new List<string>();
        AddCommonEngineArguments(args, toolchain);

        args.Add("-f");
        args.Add(request.SelectedQuality.FormatSelector);

        switch (mode)
        {
            case LivestreamRecordingMode.RecordFromNow:
                args.Add("--no-live-from-start");
                break;

            case LivestreamRecordingMode.RecordFromStartUntilEnd:
            case LivestreamRecordingMode.RecordFromStartToLiveEdge:
                // Empirically verified on YouTube 2026: --live-from-start serves separate concurrent DASH
                // streams (http_dash_segments_generator) where yt-dlp ignores --downloader ffmpeg -t and
                // rejects --download-sections. Mode C is cleanly stopped near the live edge by
                // ModeCLiveEdgeWatchdog sending CTRL_C_EVENT once both tracks pass the target segment,
                // triggering yt-dlp's built-in FFmpeg merge and clean exit (code 0).
                args.Add("--live-from-start");
                args.Add("--concurrent-fragments");
                args.Add("4");
                break;
        }

        // Crash-safe live capture containers (remuxed to MP4 with +faststart after finalization if requested)
        args.Add("--hls-use-mpegts");
        args.Add("--merge-output-format");
        args.Add("mkv");

        args.Add("-o");
        args.Add($"{reservedBaseOutputPath}.%(ext)s");
        args.Add("--windows-filenames");
        args.Add("--no-playlist");

        // Aggressive live resilience for transient drops/reconnects
        args.Add("--retries");
        args.Add("20");
        args.Add("--fragment-retries");
        args.Add("infinite");
        args.Add("--retry-sleep");
        args.Add("http:exp=1:15");
        args.Add("--retry-sleep");
        args.Add("fragment:exp=1:15");
        args.Add("--socket-timeout");
        args.Add("20");

        AddProgressAndPrintArguments(args);

        args.Add("--");
        args.Add(request.SourceUrl);
        return args;
    }

    public static IReadOnlyList<string> BuildRemuxToMp4Arguments(string inputPath, string outputMp4Path)
    {
        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-i", inputPath,
            "-c", "copy",
            "-movflags", "+faststart",
            outputMp4Path
        ];
    }

    public static IReadOnlyList<string> BuildManualMuxFallbackArguments(string videoInputPath, string audioInputPath, string outputMkvPath)
    {
        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-i", videoInputPath,
            "-i", audioInputPath,
            "-c", "copy",
            "-shortest",
            outputMkvPath
        ];
    }

    private static string BuildContainerAwareFormatSelector(QualityOption quality, string container)
    {
        if (quality.Id == "best")
        {
            return container switch
            {
                "mp4" => "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba[ext=m4a]/bv*+ba/b",
                "webm" => "bv*[ext=webm]+ba[ext=webm]/bv*+ba/b",
                _ => "bv*+ba/b"
            };
        }

        if (container == "mp4" && quality.Height is > 0)
        {
            var h = quality.Height.Value;
            return $"bv*[ext=mp4][height<={h}]+ba[ext=m4a]/{quality.FormatSelector}";
        }

        return quality.FormatSelector;
    }

    private static void AddCommonEngineArguments(List<string> args, ResolvedToolchain toolchain)
    {
        args.Add("--no-js-runtimes");
        args.Add("--js-runtimes");
        args.Add($"deno:{toolchain.DenoPath}");
        args.Add("--ffmpeg-location");
        args.Add(toolchain.FFmpegDirectory);
        args.Add("--no-update");
    }

    private static void AddProgressAndPrintArguments(List<string> args)
    {
        args.Add("--newline");
        args.Add("--progress");
        args.Add("--progress-delta");
        args.Add("0.25");
        args.Add("--progress-template");
        args.Add(YtDlpProgressParser.DownloadTemplate);
        args.Add("--progress-template");
        args.Add(YtDlpProgressParser.PostProcessTemplate);
        args.Add("--print");
        args.Add(YtDlpProgressParser.AfterMovePrintTemplate);
    }
}
