using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public interface IMediaProbeService
{
    Task<MediaProbeResult> ProbeAsync(string rawUrl, CancellationToken ct = default);
}

public sealed class MediaProbeService : IMediaProbeService
{
    private readonly IToolManager _toolManager;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<MediaProbeService> _logger;

    public MediaProbeService(
        IToolManager toolManager,
        IProcessRunner processRunner,
        ILogger<MediaProbeService> logger)
    {
        _toolManager = toolManager;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<MediaProbeResult> ProbeAsync(string rawUrl, CancellationToken ct = default)
    {
        var validation = YouTubeUrlValidator.Validate(rawUrl);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedUrl))
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid YouTube URL.", nameof(rawUrl));

        var toolchain = await _toolManager.EnsureToolsReadyAsync(ct: ct);

        _logger.LogInformation("Probing media metadata for {Url}", validation.NormalizedUrl);

        var primaryArgs = BuildProbeArguments(toolchain, validation.NormalizedUrl, liveFromStart: false);
        var primaryResult = await _processRunner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = toolchain.YtDlpPath,
            Arguments = primaryArgs,
            GracefulStop = GracefulStopStrategy.KillImmediate
        }, ct);

        if (primaryResult.ExitCode != 0 || string.IsNullOrWhiteSpace(primaryResult.StandardOutput))
        {
            var classified = YtDlpErrorClassifier.Classify(primaryResult.StandardError, primaryResult.ExitCode);
            _logger.LogWarning("Probe failed (exit={ExitCode}): {Err}", primaryResult.ExitCode, classified.TechnicalSummary);
            throw new InvalidOperationException(classified.UserMessage);
        }

        var primaryWarnings = ExtractWarnings(primaryResult.StandardError);
        var probe = ParseProbeJson(primaryResult.StandardOutput, validation.NormalizedUrl, primaryWarnings);

        // For active livestreams, run a secondary --live-from-start probe so we accurately detect
        // DVR support, rolling DVR window caps, and exact formats available for Modes B & C.
        if (probe.LiveStatus == MediaLiveStatus.IsLive)
        {
            var dvrInfo = await ProbeLivestreamDvrAsync(toolchain, probe, ct);
            probe = probe with { DvrInfo = dvrInfo };
        }

        return probe;
    }

    private async Task<DvrProbeInfo> ProbeLivestreamDvrAsync(
        ResolvedToolchain toolchain,
        MediaProbeResult liveProbe,
        CancellationToken ct)
    {
        var elapsed = liveProbe.ReleaseTimestampUtc.HasValue
            ? DateTimeOffset.UtcNow - liveProbe.ReleaseTimestampUtc.Value
            : (TimeSpan?)null;
        if (elapsed.HasValue && elapsed.Value < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        try
        {
            var fromStartArgs = BuildProbeArguments(toolchain, liveProbe.CanonicalUrl, liveFromStart: true);
            var res = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.YtDlpPath,
                Arguments = fromStartArgs,
                GracefulStop = GracefulStopStrategy.KillImmediate
            }, ct);

            var warnings = ExtractWarnings(res.StandardError);
            var stdErrLower = (res.StandardError ?? string.Empty).ToLowerInvariant();

            var dvrDisabledWarning = stdErrLower.Contains("disabled dvr")
                                     || stdErrLower.Contains("cannot download from start")
                                     || stdErrLower.Contains("live_from_start is not supported");

            var windowCapWarning = warnings.FirstOrDefault(w =>
                w.Contains("Starting download from the last", StringComparison.OrdinalIgnoreCase));

            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput) || dvrDisabledWarning)
            {
                return new DvrProbeInfo(
                    IsDvrAvailable: false,
                    IsDvrWindowCapped: false,
                    StreamElapsed: elapsed,
                    WarningMessage: dvrDisabledWarning
                        ? "The broadcaster has disabled DVR/rewind for this livestream. Only 'Record from now' (Mode A) is available."
                        : "YouTube did not expose historical segments from the beginning of this livestream. Only 'Record from now' (Mode A) is available.",
                    HasMuxedHlsFromStart: false,
                    FromStartQualities: []);
            }

            var fromStartProbe = ParseProbeJson(res.StandardOutput, liveProbe.CanonicalUrl, warnings);
            var hasDvrFormats = fromStartProbe.RawFormats.Any(f => f.HasVideo && (f.IsDash || f.IsHls));
            var hasMuxedHls = fromStartProbe.RawFormats.Any(f => f.IsMuxed && f.IsHls);

            var isCapped = !string.IsNullOrWhiteSpace(windowCapWarning) || (elapsed is { TotalHours: > 120 });
            string? notice = windowCapWarning;
            if (string.IsNullOrWhiteSpace(notice) && elapsed is { TotalHours: > 12 })
            {
                notice = $"This livestream has been running for {elapsed.Value.TotalHours:F1} hours. YouTube may limit how far back DVR segments remain available.";
            }

            return new DvrProbeInfo(
                IsDvrAvailable: hasDvrFormats,
                IsDvrWindowCapped: isCapped,
                StreamElapsed: elapsed,
                WarningMessage: hasDvrFormats
                    ? notice
                    : "No DVR replay formats were exposed by YouTube. Only 'Record from now' (Mode A) is supported.",
                HasMuxedHlsFromStart: hasMuxedHls,
                FromStartQualities: fromStartProbe.AvailableQualities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DVR --live-from-start probe failed; falling back to Mode A only.");
            return new DvrProbeInfo(
                IsDvrAvailable: false,
                IsDvrWindowCapped: false,
                StreamElapsed: elapsed,
                WarningMessage: "Could not verify DVR replay segments for this livestream. 'Record from now' (Mode A) remains available.",
                HasMuxedHlsFromStart: false,
                FromStartQualities: []);
        }
    }

    public static IReadOnlyList<string> BuildProbeArguments(ResolvedToolchain toolchain, string normalizedUrl, bool liveFromStart)
    {
        var args = new List<string>
        {
            "--dump-single-json",
            "--no-playlist",
            "--no-download",
            "--no-js-runtimes",
            "--js-runtimes", $"deno:{toolchain.DenoPath}",
            "--ffmpeg-location", toolchain.FFmpegDirectory,
            "--socket-timeout", "20"
        };

        if (liveFromStart)
            args.Add("--live-from-start");

        args.Add("--");
        args.Add(normalizedUrl);
        return args;
    }

    public static MediaProbeResult ParseProbeJson(string json, string fallbackUrl, IReadOnlyList<string>? warnings = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var videoId = GetString(root, "id") ?? string.Empty;
        var canonicalUrl = GetString(root, "webpage_url") ?? fallbackUrl;
        var title = GetString(root, "title") ?? "Untitled Video";
        var channel = GetString(root, "channel") ?? GetString(root, "uploader") ?? "Unknown Channel";
        var duration = GetDouble(root, "duration");
        var thumbnail = SelectBestThumbnail(root);

        var liveStatus = ParseLiveStatus(
            GetString(root, "live_status"),
            GetBool(root, "is_live"),
            GetBool(root, "was_live"));

        DateTimeOffset? releaseTime = null;
        var releaseEpoch = GetLong(root, "release_timestamp") ?? GetLong(root, "timestamp");
        if (releaseEpoch is > 0)
        {
            releaseTime = DateTimeOffset.FromUnixTimeSeconds(releaseEpoch.Value);
        }

        var rawFormats = ParseFormats(root);
        var isLive = liveStatus == MediaLiveStatus.IsLive;
        var qualityOptions = BuildDynamicQualityOptions(rawFormats, duration, isLive);

        return new MediaProbeResult(
            VideoId: videoId,
            CanonicalUrl: canonicalUrl,
            Title: title,
            Channel: channel,
            DurationSeconds: duration,
            ThumbnailUrl: thumbnail,
            LiveStatus: liveStatus,
            ReleaseTimestampUtc: releaseTime,
            AvailableQualities: qualityOptions,
            RawFormats: rawFormats,
            DvrInfo: null,
            Warnings: warnings ?? []);
    }

    public static IReadOnlyList<QualityOption> BuildDynamicQualityOptions(
        IReadOnlyList<RawMediaFormat> rawFormats,
        double? durationSeconds,
        bool isLive)
    {
        var results = new List<QualityOption>();

        var videoFormats = rawFormats.Where(f => f.HasVideo).ToList();
        var audioFormats = rawFormats.Where(f => f.HasAudio && !f.HasVideo).ToList();
        var bestAudio = SelectBestAudio(audioFormats);
        var bestAudioSize = EstimateBytes(bestAudio, durationSeconds);

        var videoContainers = isLive
            ? new[] { "mkv", "mp4" }
            : new[] { "mp4", "mkv", "webm" };

        if (videoFormats.Count > 0)
        {
            var maxHeight = videoFormats.Max(f => f.Height ?? 0);
            var maxFps = videoFormats.Max(f => (int)Math.Round(f.Fps ?? 30));

            // Dynamic "Best Available" entry reflecting the video's true maximum quality
            results.Add(new QualityOption(
                Id: "best",
                Label: $"Best Available ({FormatHeightLabel(maxHeight)}{(maxFps >= 48 ? $" {maxFps}fps" : string.Empty)})",
                IsAudioOnly: false,
                Height: maxHeight,
                Fps: maxFps,
                CodecSummary: "Best",
                EstimatedSizeBytes: null,
                FormatSelector: isLive
                    ? "bv*+ba/b"
                    : "bv*+ba/b",
                PreferredAudioExtension: null,
                RequiresFFmpeg: true,
                SupportedContainers: videoContainers,
                IsMuxedHls: videoFormats.Any(f => f.Height == maxHeight && f.IsMuxed && f.IsHls)));

            // Group by actual available heights descending, separating high-FPS (>= 48fps) if both exist
            var heightGroups = videoFormats
                .Where(f => f.Height is >= 144)
                .GroupBy(f => (Height: f.Height!.Value, HighFps: (f.Fps ?? 30) >= 48))
                .OrderByDescending(g => g.Key.Height)
                .ThenByDescending(g => g.Key.HighFps);

            foreach (var group in heightGroups)
            {
                var height = group.Key.Height;
                var highFps = group.Key.HighFps;
                var candidates = group.ToList();

                // Pick representative best stream for estimate & codec summary
                var representative = candidates
                    .OrderByDescending(c => c.IsMuxed && isLive ? 1 : 0)
                    .ThenByDescending(c => ScoreCodecForCompatibility(c.VideoCodec))
                    .ThenByDescending(c => c.TbrKbps ?? 0)
                    .First();

                var fpsVal = (int)Math.Round(representative.Fps ?? (highFps ? 60 : 30));
                var codecs = candidates
                    .Select(c => FriendlyCodecName(c.VideoCodec))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();
                var codecSummary = codecs.Count > 0 ? string.Join("/", codecs) : "Video";

                var estVideo = EstimateBytes(representative, durationSeconds);
                long? estTotal = representative.IsMuxed
                    ? estVideo
                    : (estVideo.HasValue && bestAudioSize.HasValue ? estVideo.Value + bestAudioSize.Value : estVideo);

                var fpsFilter = highFps ? "[fps>45]" : "[fps<=45]";
                var hasStandardAndHighFps = videoFormats.Any(f => f.Height == height && (f.Fps ?? 30) < 48)
                                            && videoFormats.Any(f => f.Height == height && (f.Fps ?? 30) >= 48);

                var heightClause = hasStandardAndHighFps
                    ? $"[height<={height}]{fpsFilter}"
                    : $"[height<={height}]";

                var hasMuxedHls = candidates.Any(c => c.IsMuxed && c.IsHls);

                // Prefer H.264/MP4-friendly or exact height match, falling back cleanly across codecs
                var formatSelector = isLive
                    ? $"bv*{heightClause}+ba/b{heightClause}/bv*[height<={height}]+ba/b[height<={height}]/b"
                    : $"bv*{heightClause}+ba/b{heightClause}/bv*[height<={height}]+ba/b[height<={height}]";

                var sizeTag = estTotal is > 0 && !isLive ? $" • ~{FormatSize(estTotal.Value)}" : string.Empty;
                var fpsTag = fpsVal >= 48 ? $" {fpsVal}fps" : string.Empty;
                var hdrTag = candidates.Any(c => c.DynamicRange?.Contains("HDR", StringComparison.OrdinalIgnoreCase) == true) ? " HDR" : string.Empty;
                var label = $"{FormatHeightLabel(height)}{fpsTag}{hdrTag} ({codecSummary}){sizeTag}";

                results.Add(new QualityOption(
                    Id: $"video-{height}-{(highFps ? "hfr" : "std")}",
                    Label: label,
                    IsAudioOnly: false,
                    Height: height,
                    Fps: fpsVal,
                    CodecSummary: codecSummary,
                    EstimatedSizeBytes: estTotal,
                    FormatSelector: formatSelector,
                    PreferredAudioExtension: null,
                    RequiresFFmpeg: !representative.IsMuxed,
                    SupportedContainers: videoContainers,
                    IsMuxedHls: hasMuxedHls));
            }
        }

        // Audio-only options for standard VOD / completed streams (when audio streams exist)
        if (!isLive && (audioFormats.Count > 0 || videoFormats.Any(v => v.HasAudio)))
        {
            var audioSizeTag = bestAudioSize is > 0 ? $" • ~{FormatSize(bestAudioSize.Value)}" : string.Empty;

            results.Add(new QualityOption(
                Id: "audio-m4a",
                Label: $"Audio Only — M4A / AAC (Best){audioSizeTag}",
                IsAudioOnly: true,
                Height: null,
                Fps: null,
                CodecSummary: "AAC",
                EstimatedSizeBytes: bestAudioSize,
                FormatSelector: "ba[ext=m4a]/ba/b",
                PreferredAudioExtension: "m4a",
                RequiresFFmpeg: true,
                SupportedContainers: ["m4a"]));

            results.Add(new QualityOption(
                Id: "audio-mp3",
                Label: $"Audio Only — MP3 (320 kbps){audioSizeTag}",
                IsAudioOnly: true,
                Height: null,
                Fps: null,
                CodecSummary: "MP3",
                EstimatedSizeBytes: bestAudioSize,
                FormatSelector: "ba/b",
                PreferredAudioExtension: "mp3",
                RequiresFFmpeg: true,
                SupportedContainers: ["mp3"]));

            results.Add(new QualityOption(
                Id: "audio-opus",
                Label: $"Audio Only — Opus (Original){audioSizeTag}",
                IsAudioOnly: true,
                Height: null,
                Fps: null,
                CodecSummary: "Opus",
                EstimatedSizeBytes: bestAudioSize,
                FormatSelector: "ba[acodec^=opus]/ba/b",
                PreferredAudioExtension: "opus",
                RequiresFFmpeg: true,
                SupportedContainers: ["opus"]));
        }

        // Fallback when YouTube returned only an opaque manifest without standard height metadata
        if (results.Count == 0)
        {
            results.Add(new QualityOption(
                Id: "best",
                Label: "Best Available",
                IsAudioOnly: false,
                Height: null,
                Fps: null,
                CodecSummary: "Auto",
                EstimatedSizeBytes: null,
                FormatSelector: "bv*+ba/b",
                PreferredAudioExtension: null,
                RequiresFFmpeg: true,
                SupportedContainers: videoContainers));
        }

        return results;
    }

    private static List<RawMediaFormat> ParseFormats(JsonElement root)
    {
        var list = new List<RawMediaFormat>();
        if (!root.TryGetProperty("formats", out var formatsEl) || formatsEl.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in formatsEl.EnumerateArray())
        {
            var id = GetString(el, "format_id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var ext = GetString(el, "ext") ?? string.Empty;
            var protocol = GetString(el, "protocol") ?? string.Empty;

            // Skip storyboard image sheets
            if (protocol.Equals("mhtml", StringComparison.OrdinalIgnoreCase)
                || ext.Equals("mhtml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new RawMediaFormat(
                FormatId: id,
                Ext: ext,
                Protocol: protocol,
                Width: GetInt(el, "width"),
                Height: GetInt(el, "height"),
                Fps: GetDouble(el, "fps"),
                VideoCodec: GetString(el, "vcodec"),
                AudioCodec: GetString(el, "acodec"),
                TbrKbps: GetDouble(el, "tbr"),
                FileSizeBytes: GetLong(el, "filesize"),
                FileSizeApproxBytes: GetLong(el, "filesize_approx"),
                FormatNote: GetString(el, "format_note"),
                DynamicRange: GetString(el, "dynamic_range")));
        }

        return list;
    }

    private static RawMediaFormat? SelectBestAudio(List<RawMediaFormat> audioFormats)
    {
        return audioFormats
            .OrderByDescending(f => f.TbrKbps ?? 0)
            .ThenByDescending(f => f.BestKnownSizeBytes ?? 0)
            .FirstOrDefault();
    }

    private static long? EstimateBytes(RawMediaFormat? format, double? durationSeconds)
    {
        if (format is null)
            return null;

        if (format.BestKnownSizeBytes is > 0)
            return format.BestKnownSizeBytes;

        if (format.TbrKbps is > 0 && durationSeconds is > 0)
            return (long)(format.TbrKbps.Value * 1000.0 / 8.0 * durationSeconds.Value);

        return null;
    }

    private static string? SelectBestThumbnail(JsonElement root)
    {
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            string? bestJpg = null;
            int bestScore = -1;

            foreach (var item in thumbs.EnumerateArray())
            {
                var url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;

                // WPF BitmapImage loads JPEG/PNG reliably without WebP codec dependency
                var isJpgOrPng = url.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
                                 || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
                                 || url.Contains(".png", StringComparison.OrdinalIgnoreCase);

                var width = GetInt(item, "width") ?? 0;
                var score = (isJpgOrPng ? 100_000 : 0) + Math.Min(width, 1920);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestJpg = url;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestJpg))
                return bestJpg;
        }

        return GetString(root, "thumbnail");
    }

    private static MediaLiveStatus ParseLiveStatus(string? liveStatus, bool? isLive, bool? wasLive)
    {
        if (!string.IsNullOrWhiteSpace(liveStatus))
        {
            return liveStatus.ToLowerInvariant() switch
            {
                "is_live" => MediaLiveStatus.IsLive,
                "is_upcoming" => MediaLiveStatus.IsUpcoming,
                "was_live" => MediaLiveStatus.WasLive,
                "post_live" => MediaLiveStatus.PostLive,
                "not_live" => MediaLiveStatus.NotLive,
                _ => MediaLiveStatus.Unknown
            };
        }

        if (isLive == true) return MediaLiveStatus.IsLive;
        if (wasLive == true) return MediaLiveStatus.WasLive;
        return MediaLiveStatus.NotLive;
    }

    private static List<string> ExtractWarnings(string? stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr))
            return [];

        return stdErr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["WARNING:".Length..].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static int ScoreCodecForCompatibility(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return 0;
        var lower = codec.ToLowerInvariant();
        if (lower.StartsWith("avc1") || lower.Contains("h264")) return 3;
        if (lower.StartsWith("vp9") || lower.StartsWith("vp09")) return 2;
        if (lower.StartsWith("av01")) return 1;
        return 0;
    }

    private static string FriendlyCodecName(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var lower = codec.ToLowerInvariant();
        if (lower.StartsWith("av01")) return "AV1";
        if (lower.StartsWith("vp9") || lower.StartsWith("vp09")) return "VP9";
        if (lower.StartsWith("avc1") || lower.Contains("h264")) return "H.264";
        if (lower.StartsWith("hev1") || lower.StartsWith("hvc1")) return "HEVC";
        return codec.Split('.')[0].ToUpperInvariant();
    }

    private static string FormatHeightLabel(int height) => height switch
    {
        >= 4320 => $"{height}p (8K)",
        >= 2160 => $"{height}p (4K)",
        >= 1440 => $"{height}p (2K)",
        _ => $"{height}p"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? GetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        return null;
    }

    private static long? GetLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)Math.Round(d);
        return null;
    }

    private static double? GetDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
}
