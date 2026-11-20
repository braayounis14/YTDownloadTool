using System.Globalization;
using System.Text.RegularExpressions;

namespace YTDownloadTool.Core.Downloads;

public enum ParsedOutputKind
{
    None = 0,
    DownloadProgress = 1,
    PostProcess = 2,
    FFmpegProgress = 3,
    FinalFilePath = 4,
    DestinationPath = 5,
    WarningNotice = 6,
    LiveHeadWait = 7
}

public sealed record ParsedYtDlpLine(
    ParsedOutputKind Kind,
    string? Status = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? SpeedBytesPerSecond = null,
    TimeSpan? Eta = null,
    int? FragmentIndex = null,
    int? FragmentCount = null,
    bool HasVideo = false,
    bool HasAudio = false,
    string? FormatId = null,
    string? StreamChannel = null,
    string? PostProcessor = null,
    TimeSpan? MediaTime = null,
    string? FilePath = null,
    string? Notice = null);

public static partial class YtDlpProgressParser
{
    public const string ProgressPrefix = "YTDL_PROG|";
    public const string PostProcessPrefix = "YTDL_PP|";
    public const string FinalFilePrefix = "YTDL_FINAL_FILE:";

    public const string DownloadTemplate =
        "download:YTDL_PROG|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s|%(info.vcodec)s|%(info.acodec)s|%(info.format_id)s";

    public const string PostProcessTemplate =
        "postprocess:YTDL_PP|%(progress.status)s|%(progress.postprocessor)s";

    public const string AfterMovePrintTemplate =
        "after_move:YTDL_FINAL_FILE:%(filepath)s";

    // Matches concurrent multi-stream prefix emitted by yt-dlp --live-from-start (e.g. "1: YTDL_PROG|..." / "2: YTDL_PROG|...")
    [GeneratedRegex(@"^(?<ch>\d+):\s+(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex MultiStreamPrefixRegex();

    [GeneratedRegex(@"time=(\d{2,}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex FFmpegTimeRegex();

    [GeneratedRegex(@"size=\s*(\d+(?:\.\d+)?)\s*(Ki?B|Mi?B|Gi?B|kB|MB|GB|B)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FFmpegSizeRegex();

    [GeneratedRegex(@"\[download\]\s+Destination:\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex DestinationRegex();

    [GeneratedRegex(@"\[Merger\]\s+Merging formats into\s+""(.+)""", RegexOptions.Compiled)]
    private static partial Regex MergerTargetRegex();

    public static ParsedYtDlpLine ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new ParsedYtDlpLine(ParsedOutputKind.None);

        var trimmed = line.Trim();
        string? streamChannel = null;

        var channelMatch = MultiStreamPrefixRegex().Match(trimmed);
        if (channelMatch.Success)
        {
            streamChannel = channelMatch.Groups["ch"].Value;
            trimmed = channelMatch.Groups["rest"].Value.TrimStart();
        }

        if (trimmed.StartsWith(FinalFilePrefix, StringComparison.Ordinal))
        {
            var path = trimmed[FinalFilePrefix.Length..].Trim();
            return new ParsedYtDlpLine(ParsedOutputKind.FinalFilePath, FilePath: path);
        }

        if (trimmed.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return ParseStructuredProgress(trimmed[ProgressPrefix.Length..], streamChannel);
        }

        if (trimmed.StartsWith(PostProcessPrefix, StringComparison.Ordinal))
        {
            var parts = trimmed[PostProcessPrefix.Length..].Split('|');
            var status = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            var pp = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            return new ParsedYtDlpLine(ParsedOutputKind.PostProcess, Status: status, PostProcessor: pp);
        }

        var destMatch = DestinationRegex().Match(trimmed);
        if (destMatch.Success)
        {
            return new ParsedYtDlpLine(ParsedOutputKind.DestinationPath, FilePath: destMatch.Groups[1].Value.Trim());
        }

        var mergerMatch = MergerTargetRegex().Match(trimmed);
        if (mergerMatch.Success)
        {
            return new ParsedYtDlpLine(ParsedOutputKind.DestinationPath, FilePath: mergerMatch.Groups[1].Value.Trim());
        }

        if (trimmed.Contains("time=", StringComparison.Ordinal) &&
            (trimmed.Contains("frame=", StringComparison.Ordinal) || trimmed.Contains("size=", StringComparison.Ordinal)))
        {
            var timeMatch = FFmpegTimeRegex().Match(trimmed);
            if (timeMatch.Success)
            {
                var hours = int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var mins = int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var secs = double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                var mediaTime = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);

                long? bytes = null;
                var sizeMatch = FFmpegSizeRegex().Match(trimmed);
                if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sizeVal))
                {
                    var unit = sizeMatch.Groups[2].Value.ToLowerInvariant();
                    bytes = unit switch
                    {
                        "kib" or "kb" => (long)(sizeVal * 1024),
                        "mib" or "mb" => (long)(sizeVal * 1024 * 1024),
                        "gib" or "gb" => (long)(sizeVal * 1024 * 1024 * 1024),
                        _ => (long)sizeVal
                    };
                }

                return new ParsedYtDlpLine(ParsedOutputKind.FFmpegProgress, DownloadedBytes: bytes, MediaTime: mediaTime);
            }
        }

        if (trimmed.Contains("Waiting for new fragments", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Giving up after", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedYtDlpLine(ParsedOutputKind.LiveHeadWait, Notice: trimmed);
        }

        if (trimmed.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("Starting download from the last", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("disabled DVR", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("live_from_start", StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedYtDlpLine(ParsedOutputKind.WarningNotice, Notice: trimmed["WARNING:".Length..].Trim());
            }
        }

        return new ParsedYtDlpLine(ParsedOutputKind.None);
    }

    private static ParsedYtDlpLine ParseStructuredProgress(string payload, string? streamChannel)
    {
        var parts = payload.Split('|');
        if (parts.Length < 8)
            return new ParsedYtDlpLine(ParsedOutputKind.None);

        var status = parts[0].Trim();
        var downloaded = TryParseLong(parts[1]);
        var total = TryParseLong(parts[2]) ?? TryParseLong(parts[3]);
        var speed = TryParseDouble(parts[4]);
        var etaSeconds = TryParseDouble(parts[5]);

        // Only trust fragment_index on active downloading lines (yt-dlp emits NA/empty on 'finished')
        var isFinished = status.Equals("finished", StringComparison.OrdinalIgnoreCase);
        var fragIdx = isFinished ? null : TryParseInt(parts[6]);
        var fragCount = TryParseInt(parts[7]);

        var vcodec = parts.Length > 8 ? parts[8].Trim() : string.Empty;
        var acodec = parts.Length > 9 ? parts[9].Trim() : string.Empty;
        var formatId = parts.Length > 10 && !IsEmptyOrNone(parts[10]) ? parts[10].Trim() : null;
        var hasVideo = !IsEmptyOrNone(vcodec);
        var hasAudio = !IsEmptyOrNone(acodec);

        TimeSpan? eta = etaSeconds is >= 0 and < 86400 * 30
            ? TimeSpan.FromSeconds(etaSeconds.Value)
            : null;

        return new ParsedYtDlpLine(
            Kind: ParsedOutputKind.DownloadProgress,
            Status: status,
            DownloadedBytes: downloaded,
            TotalBytes: total,
            SpeedBytesPerSecond: speed,
            Eta: eta,
            FragmentIndex: fragIdx,
            FragmentCount: fragCount,
            HasVideo: hasVideo,
            HasAudio: hasAudio,
            FormatId: formatId,
            StreamChannel: streamChannel);
    }

    private static bool IsEmptyOrNone(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Equals("NA", StringComparison.OrdinalIgnoreCase)
        || value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static long? TryParseLong(string value)
    {
        if (IsEmptyOrNone(value))
            return null;

        if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;

        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return (long)d;

        return null;
    }

    private static int? TryParseInt(string value)
    {
        if (IsEmptyOrNone(value))
            return null;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static double? TryParseDouble(string value)
    {
        if (IsEmptyOrNone(value))
            return null;

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
            ? d
            : null;
    }
}
