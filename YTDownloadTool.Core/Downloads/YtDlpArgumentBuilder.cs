using System.Globalization;
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

        // Structured progress & deterministic final filename output
        AddProgressAndPrintArguments(args);

        args.Add("--");
        args.Add(request.SourceUrl);
        return args;
    }

    public static IReadOnlyList<string> BuildLivestreamArguments(
        ResolvedToolchain toolchain,
        DownloadJobRequest request,
        string reservedBaseOutputPath,
        LivestreamRecordingMode mode,
        TimeSpan? modeCTargetDuration = null,
        bool preferMuxedHlsForModeC = false)
    {
        var args = new List<string>();
        AddCommonEngineArguments(args, toolchain);

        // Always record live streams into crash-safe MKV/MPEG-TS during capture;
        // if the user asked for MP4, we remux cleanly after finalization.
        var formatSelector = request.SelectedQuality.FormatSelector;
        if (mode == LivestreamRecordingMode.RecordFromStartToLiveEdge && preferMuxedHlsForModeC)
        {
            var h = request.SelectedQuality.Height;
            formatSelector = h is > 0
                ? $"b[protocol*=m3u8][height<={h}]/bv*[height<={h}]+ba/b[height<={h}]/b"
                : "b[protocol*=m3u8]/bv*+ba/b";
        }

        args.Add("-f");
        args.Add(formatSelector);

        switch (mode)
        {
            case LivestreamRecordingMode.RecordFromNow:
                args.Add("--no-live-from-start");
                break;

            case LivestreamRecordingMode.RecordFromStartUntilEnd:
                args.Add("--live-from-start");
                args.Add("--concurrent-fragments");
                args.Add("4");
                break;

            case LivestreamRecordingMode.RecordFromStartToLiveEdge:
                args.Add("--live-from-start");
                if (modeCTargetDuration is { TotalSeconds: >= 5 } target)
                {
                    var secondsStr = Math.Floor(target.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture);
                    args.Add("--downloader");
                    args.Add("ffmpeg");
                    args.Add("--downloader-args");
                    args.Add($"ffmpeg_o:-t {secondsStr}");
                }
                else
                {
                    args.Add("--concurrent-fragments");
                    args.Add("4");
                }
                break;
        }

        // Crash-safe live container settings
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
            // Prefer MP4/AAC-compatible streams at the requested height first so remuxing needs zero transcoding,
            // while still falling back gracefully if only VP9/AV1/Opus exist at 1440p/2160p.
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
