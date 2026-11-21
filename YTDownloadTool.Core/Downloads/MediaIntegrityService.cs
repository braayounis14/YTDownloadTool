using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Core.Downloads;

public sealed record MediaIntegrityProbeInfo(
    bool IsValid,
    string? IssueSummary,
    long FileSizeBytes,
    double? DurationSeconds,
    bool HasVideoStream,
    bool HasAudioStream,
    string? Resolution,
    string? VideoCodec,
    string? AudioCodec);

public sealed record MediaIntegrityResult(
    MediaIntegrityStatus Status,
    string Message,
    long FileSizeBytes,
    double? DurationSeconds,
    string? Resolution,
    string? VideoCodec,
    string? AudioCodec,
    bool WasRepaired,
    DateTimeOffset VerifiedAtUtc);

public interface IMediaIntegrityService
{
    Task<MediaIntegrityProbeInfo> ProbeFileAsync(
        ResolvedToolchain toolchain,
        string filePath,
        bool? expectVideo = null,
        bool expectAudio = true,
        CancellationToken ct = default);

    Task<MediaIntegrityResult> VerifyAndRepairIfNeededAsync(
        ResolvedToolchain toolchain,
        string filePath,
        bool? expectVideo = null,
        bool expectAudio = true,
        string? workingTempDirectory = null,
        CancellationToken ct = default);
}

public sealed class MediaIntegrityService : IMediaIntegrityService
{
    private static readonly HashSet<string> FastStartContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4a", ".mov"
    };

    private static readonly HashSet<string> AudioOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav", ".aac"
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<MediaIntegrityService> _logger;

    public MediaIntegrityService(IProcessRunner processRunner, ILogger<MediaIntegrityService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public static IReadOnlyList<string> BuildFfprobeArguments(string filePath)
    {
        return
        [
            "-v", "error",
            "-show_format",
            "-show_streams",
            "-of", "json",
            filePath
        ];
    }

    /// <summary>
    /// Builds container-aware lossless FFmpeg remux arguments.
    /// Only passes -movflags +faststart for .mp4, .m4a, and .mov containers.
    /// </summary>
    public static IReadOnlyList<string> BuildLosslessRepairArguments(string inputPath, string outputPath)
    {
        var ext = Path.GetExtension(outputPath);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-err_detect", "ignore_err",
            "-i", inputPath,
            "-c", "copy"
        };

        if (FastStartContainers.Contains(ext))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(outputPath);
        return args;
    }

    public async Task<MediaIntegrityProbeInfo> ProbeFileAsync(
        ResolvedToolchain toolchain,
        string filePath,
        bool? expectVideo = null,
        bool expectAudio = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new MediaIntegrityProbeInfo(false, "File does not exist on disk.", 0, null, false, false, null, null, null);
        }

        var fileSize = new FileInfo(filePath).Length;
        if (fileSize <= 0)
        {
            return new MediaIntegrityProbeInfo(false, "File is empty (0 bytes).", 0, null, false, false, null, null, null);
        }

        var ffprobePath = ResolveFfprobePath(toolchain);
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            return new MediaIntegrityProbeInfo(false, "ffprobe executable could not be resolved.", fileSize, null, false, false, null, null, null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        var result = await _processRunner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = ffprobePath,
            Arguments = BuildFfprobeArguments(filePath),
            GracefulStop = GracefulStopStrategy.KillImmediate,
            GracefulStopTimeout = TimeSpan.FromSeconds(2)
        }, timeoutCts.Token).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var errLine = YtDlpErrorClassifier.ExtractPrimaryErrorLine(result.StandardError);
            var reason = string.IsNullOrWhiteSpace(errLine)
                ? $"ffprobe reported container/stream error (exit code {result.ExitCode})."
                : $"ffprobe error: {errLine}";
            return new MediaIntegrityProbeInfo(false, reason, fileSize, null, false, false, null, null, null);
        }

        return ParseFfprobeJson(result.StandardOutput, filePath, fileSize, expectVideo, expectAudio);
    }

    public static MediaIntegrityProbeInfo ParseFfprobeJson(
        string json,
        string filePath,
        long fileSizeBytes,
        bool? expectVideo,
        bool expectAudio)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MediaIntegrityProbeInfo(false, "ffprobe returned empty metadata.", fileSizeBytes, null, false, false, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double? formatDuration = null;
            if (root.TryGetProperty("format", out var formatEl)
                && formatEl.ValueKind == JsonValueKind.Object
                && formatEl.TryGetProperty("duration", out var durProp))
            {
                formatDuration = TryParseDouble(durProp);
            }

            var hasVideo = false;
            var hasAudio = false;
            string? resolution = null;
            string? videoCodec = null;
            string? audioCodec = null;

            if (root.TryGetProperty("streams", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streamsEl.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object)
                        continue;

                    var codecType = stream.TryGetProperty("codec_type", out var ctProp) ? ctProp.GetString() : null;
                    var codecName = stream.TryGetProperty("codec_name", out var cnProp) ? cnProp.GetString() : null;

                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ignore embedded cover-art / thumbnail attachments (disposition.attached_pic == 1)
                        if (IsAttachedPic(stream))
                            continue;

                        hasVideo = true;
                        videoCodec ??= NormalizeCodecName(codecName);

                        if (resolution is null
                            && stream.TryGetProperty("width", out var wProp)
                            && stream.TryGetProperty("height", out var hProp)
                            && wProp.TryGetInt32(out var width) && width > 0
                            && hProp.TryGetInt32(out var height) && height > 0)
                        {
                            resolution = $"{width}x{height}";
                        }
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAudio = true;
                        audioCodec ??= NormalizeCodecName(codecName);
                    }
                }
            }

            var issues = new List<string>();
            if (formatDuration is null || formatDuration.Value <= 0 || double.IsNaN(formatDuration.Value) || double.IsInfinity(formatDuration.Value))
            {
                issues.Add("missing or zero container duration");
            }

            var ext = Path.GetExtension(filePath);
            var inferredAudioOnly = expectVideo == false || (expectVideo is null && AudioOnlyExtensions.Contains(ext));

            if (expectVideo == true && !hasVideo)
            {
                issues.Add("missing expected video stream");
            }

            if (expectAudio && !hasAudio)
            {
                issues.Add("missing expected audio stream");
            }

            if (!inferredAudioOnly && expectVideo is null && !hasVideo && !hasAudio)
            {
                issues.Add("no playable audio or video streams found");
            }

            var isValid = issues.Count == 0;
            return new MediaIntegrityProbeInfo(
                IsValid: isValid,
                IssueSummary: isValid ? null : $"Integrity issue detected: {string.Join("; ", issues)}.",
                FileSizeBytes: fileSizeBytes,
                DurationSeconds: formatDuration is > 0 ? formatDuration : null,
                HasVideoStream: hasVideo,
                HasAudioStream: hasAudio,
                Resolution: resolution,
                VideoCodec: videoCodec,
                AudioCodec: audioCodec);
        }
        catch (JsonException ex)
        {
            return new MediaIntegrityProbeInfo(false, $"Could not parse ffprobe output: {ex.Message}", fileSizeBytes, null, false, false, null, null, null);
        }
    }

    public async Task<MediaIntegrityResult> VerifyAndRepairIfNeededAsync(
        ResolvedToolchain toolchain,
        string filePath,
        bool? expectVideo = null,
        bool expectAudio = true,
        string? workingTempDirectory = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var initial = await ProbeFileAsync(toolchain, filePath, expectVideo, expectAudio, ct).ConfigureAwait(false);

        if (initial.IsValid)
        {
            _logger.LogInformation(
                "Media integrity verified for '{FileName}': status={Status}, size={SizeBytes}B, duration={DurationSec:F1}s, resolution={Resolution}, codecs={VideoCodec}/{AudioCodec}.",
                Path.GetFileName(filePath),
                MediaIntegrityStatus.Verified,
                initial.FileSizeBytes,
                initial.DurationSeconds ?? 0,
                initial.Resolution ?? "none",
                initial.VideoCodec ?? "none",
                initial.AudioCodec ?? "none");

            return new MediaIntegrityResult(
                Status: MediaIntegrityStatus.Verified,
                Message: "Verified healthy via ffprobe.",
                FileSizeBytes: initial.FileSizeBytes,
                DurationSeconds: initial.DurationSeconds,
                Resolution: initial.Resolution,
                VideoCodec: initial.VideoCodec,
                AudioCodec: initial.AudioCodec,
                WasRepaired: false,
                VerifiedAtUtc: now);
        }

        // Cannot repair missing or empty files
        if (initial.FileSizeBytes <= 0 || !File.Exists(filePath))
        {
            _logger.LogInformation(
                "Media integrity check flagged '{FileName}' as {Status}: {Issue}",
                Path.GetFileName(filePath),
                MediaIntegrityStatus.NeedsAttention,
                initial.IssueSummary ?? "File is missing or empty.");

            return new MediaIntegrityResult(
                Status: MediaIntegrityStatus.NeedsAttention,
                Message: initial.IssueSummary ?? "File is missing or empty.",
                FileSizeBytes: initial.FileSizeBytes,
                DurationSeconds: initial.DurationSeconds,
                Resolution: initial.Resolution,
                VideoCodec: initial.VideoCodec,
                AudioCodec: initial.AudioCodec,
                WasRepaired: false,
                VerifiedAtUtc: now);
        }

        _logger.LogWarning(
            "Media integrity check failed for '{FileName}': {Issue}. Attempting lossless FFmpeg remux repair...",
            Path.GetFileName(filePath), initial.IssueSummary);

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".mkv";

        var repairDir = !string.IsNullOrWhiteSpace(workingTempDirectory)
            ? workingTempDirectory
            : (Path.GetDirectoryName(filePath) ?? Path.GetTempPath());
        Directory.CreateDirectory(repairDir);

        var repairedTempPath = Path.Combine(repairDir, $".ytdl_repair_{Guid.NewGuid():N}{ext}");
        try
        {
            var repairArgs = BuildLosslessRepairArguments(filePath, repairedTempPath);
            var repairRes = await _processRunner.RunAsync(new ProcessRunRequest
            {
                ExecutablePath = toolchain.FFmpegPath,
                Arguments = repairArgs,
                GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
                GracefulStopTimeout = TimeSpan.FromSeconds(5)
            }, ct).ConfigureAwait(false);

            if (repairRes.ExitCode == 0 && File.Exists(repairedTempPath) && new FileInfo(repairedTempPath).Length > 0)
            {
                var recheck = await ProbeFileAsync(toolchain, repairedTempPath, expectVideo, expectAudio, ct).ConfigureAwait(false);
                if (recheck.IsValid)
                {
                    await ReplaceFileAtomicallySafeAsync(repairedTempPath, filePath, ct).ConfigureAwait(false);
                    var finalSize = File.Exists(filePath) ? new FileInfo(filePath).Length : recheck.FileSizeBytes;
                    _logger.LogInformation(
                        "Media integrity repaired for '{FileName}': status={Status}, size={SizeBytes}B, duration={DurationSec:F1}s, resolution={Resolution}, codecs={VideoCodec}/{AudioCodec}.",
                        Path.GetFileName(filePath),
                        MediaIntegrityStatus.Repaired,
                        finalSize,
                        recheck.DurationSeconds ?? 0,
                        recheck.Resolution ?? "none",
                        recheck.VideoCodec ?? "none",
                        recheck.AudioCodec ?? "none");

                    return new MediaIntegrityResult(
                        Status: MediaIntegrityStatus.Repaired,
                        Message: "Container index/metadata repaired losslessly and re-verified.",
                        FileSizeBytes: finalSize,
                        DurationSeconds: recheck.DurationSeconds,
                        Resolution: recheck.Resolution,
                        VideoCodec: recheck.VideoCodec,
                        AudioCodec: recheck.AudioCodec,
                        WasRepaired: true,
                        VerifiedAtUtc: DateTimeOffset.UtcNow);
                }
            }

            _logger.LogWarning(
                "Lossless FFmpeg repair did not produce a verified output for '{FileName}'; preserving original file (status={Status}).",
                Path.GetFileName(filePath), MediaIntegrityStatus.NeedsAttention);
            return new MediaIntegrityResult(
                Status: MediaIntegrityStatus.NeedsAttention,
                Message: $"{initial.IssueSummary} Lossless repair did not resolve the issue; original file preserved.",
                FileSizeBytes: initial.FileSizeBytes,
                DurationSeconds: initial.DurationSeconds,
                Resolution: initial.Resolution,
                VideoCodec: initial.VideoCodec,
                AudioCodec: initial.AudioCodec,
                WasRepaired: false,
                VerifiedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Exception during lossless FFmpeg repair of '{FileName}'; preserving original file (status={Status}).",
                Path.GetFileName(filePath), MediaIntegrityStatus.NeedsAttention);
            return new MediaIntegrityResult(
                Status: MediaIntegrityStatus.NeedsAttention,
                Message: $"{initial.IssueSummary} Repair error: {ex.Message}",
                FileSizeBytes: initial.FileSizeBytes,
                DurationSeconds: initial.DurationSeconds,
                Resolution: initial.Resolution,
                VideoCodec: initial.VideoCodec,
                AudioCodec: initial.AudioCodec,
                WasRepaired: false,
                VerifiedAtUtc: DateTimeOffset.UtcNow);
        }
        finally
        {
            try
            {
                if (File.Exists(repairedTempPath))
                    File.Delete(repairedTempPath);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private static async Task ReplaceFileAtomicallySafeAsync(string repairedSourcePath, string targetPath, CancellationToken ct)
    {
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        var sameVolume = string.Equals(
            DiskSpaceReservationLedger.TryNormalizeDriveRoot(repairedSourcePath),
            DiskSpaceReservationLedger.TryNormalizeDriveRoot(targetPath),
            StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            File.Move(repairedSourcePath, targetPath, overwrite: true);
            return;
        }

        // Cross-volume: stage onto destination volume first, then atomically replace target
        var stagedOnTargetVol = Path.Combine(targetDir, $".ytdl_replace_{Guid.NewGuid():N}{Path.GetExtension(targetPath)}");
        try
        {
            await using (var src = new FileStream(repairedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            await using (var dst = new FileStream(stagedOnTargetVol, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(stagedOnTargetVol, targetPath, overwrite: true);
            try { File.Delete(repairedSourcePath); } catch { }
        }
        catch
        {
            try { if (File.Exists(stagedOnTargetVol)) File.Delete(stagedOnTargetVol); } catch { }
            throw;
        }
    }

    private static string ResolveFfprobePath(ResolvedToolchain toolchain)
    {
        if (!string.IsNullOrWhiteSpace(toolchain.FFprobePath))
            return toolchain.FFprobePath;

        if (!string.IsNullOrWhiteSpace(toolchain.FFmpegDirectory))
            return Path.Combine(toolchain.FFmpegDirectory, "ffprobe.exe");

        return string.Empty;
    }

    private static bool IsAttachedPic(JsonElement stream)
    {
        if (!stream.TryGetProperty("disposition", out var disp) || disp.ValueKind != JsonValueKind.Object)
            return false;

        if (!disp.TryGetProperty("attached_pic", out var picProp))
            return false;

        return picProp.ValueKind switch
        {
            JsonValueKind.Number => picProp.TryGetInt32(out var val) && val == 1,
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(picProp.GetString(), "1", StringComparison.Ordinal)
                                    || string.Equals(picProp.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static double? TryParseDouble(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? NormalizeCodecName(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;

        return codec.Trim().ToLowerInvariant();
    }
}
