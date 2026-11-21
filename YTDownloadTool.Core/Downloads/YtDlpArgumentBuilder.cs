using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public static class YtDlpArgumentBuilder
{
    public static IReadOnlyList<string> BuildStandardDownloadArguments(
        ResolvedToolchain toolchain,
        DownloadJobRequest request,
        string finalSaveDirectory,
        string jobTempDirectory,
        string relativeBaseFileName,
        int perDownloadSpeedLimitKbps = 0,
        bool resumeExisting = false)
    {
        ValidateRelativeTemplateName(relativeBaseFileName);

        var args = new List<string>();
        AddCommonEngineArguments(args, toolchain, perDownloadSpeedLimitKbps);

        if (resumeExisting)
        {
            args.Add("--continue");
        }

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

        // CRITICAL yt-dlp rule: -P home/temp only take effect when -o is a RELATIVE filename template.
        AddIsolatedPathsAndOutputArguments(args, finalSaveDirectory, jobTempDirectory, relativeBaseFileName);

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
        string finalSaveDirectory,
        string jobTempDirectory,
        string relativeBaseFileName,
        LivestreamRecordingMode mode,
        int perDownloadSpeedLimitKbps = 0)
    {
        ValidateRelativeTemplateName(relativeBaseFileName);

        var args = new List<string>();
        AddCommonEngineArguments(args, toolchain, perDownloadSpeedLimitKbps);

        args.Add("-f");
        args.Add(request.SelectedQuality.FormatSelector);

        switch (mode)
        {
            case LivestreamRecordingMode.RecordFromNow:
                args.Add("--no-live-from-start");
                break;

            case LivestreamRecordingMode.RecordFromStartUntilEnd:
            case LivestreamRecordingMode.RecordFromStartToLiveEdge:
                args.Add("--live-from-start");
                args.Add("--concurrent-fragments");
                args.Add("4");
                break;
        }

        // Crash-safe live capture containers; all fragments and intermediate parts stay inside jobTempDirectory
        args.Add("--hls-use-mpegts");
        args.Add("--merge-output-format");
        args.Add("mkv");

        // When user requests MP4 for a livestream, stage the final yt-dlp MKV inside jobTempDirectory first
        // so we can remux MKV -> MP4 (+faststart) in temp before moving ONLY the final MP4 to finalSaveDirectory.
        var ytDlpHomeDir = request.OutputContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            ? jobTempDirectory
            : finalSaveDirectory;
        AddIsolatedPathsAndOutputArguments(args, ytDlpHomeDir, jobTempDirectory, relativeBaseFileName);

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

    private static void AddIsolatedPathsAndOutputArguments(
        List<string> args,
        string homeDirectory,
        string tempDirectory,
        string relativeBaseFileName)
    {
        args.Add("-P");
        args.Add($"home:{Path.GetFullPath(homeDirectory)}");
        args.Add("-P");
        args.Add($"temp:{Path.GetFullPath(tempDirectory)}");
        args.Add("-o");
        args.Add($"{relativeBaseFileName}.%(ext)s");
        args.Add("--windows-filenames");
        args.Add("--no-playlist");
    }

    private static void ValidateRelativeTemplateName(string relativeBaseFileName)
    {
        if (string.IsNullOrWhiteSpace(relativeBaseFileName)
            || Path.IsPathRooted(relativeBaseFileName)
            || relativeBaseFileName.Contains(Path.DirectorySeparatorChar)
            || relativeBaseFileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Output filename template must be a relative filename (without directories) so yt-dlp honors -P home/temp.",
                nameof(relativeBaseFileName));
        }
    }

    private static void AddCommonEngineArguments(List<string> args, ResolvedToolchain toolchain, int perDownloadSpeedLimitKbps)
    {
        args.Add("--no-js-runtimes");
        args.Add("--js-runtimes");
        args.Add($"deno:{toolchain.DenoPath}");
        args.Add("--ffmpeg-location");
        args.Add(toolchain.FFmpegDirectory);
        args.Add("--no-update");

        if (perDownloadSpeedLimitKbps > 0)
        {
            args.Add("--limit-rate");
            args.Add($"{perDownloadSpeedLimitKbps}K");
        }
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
