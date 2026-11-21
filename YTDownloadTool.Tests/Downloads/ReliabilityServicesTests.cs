using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Downloads;

public sealed class ReliabilityServicesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ResolvedToolchain _toolchain;

    public ReliabilityServicesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ytdl_m2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _toolchain = new ResolvedToolchain(
            @"C:\Tools\yt-dlp\yt-dlp.exe",
            @"C:\Tools\ffmpeg",
            @"C:\Tools\ffmpeg\ffmpeg.exe",
            @"C:\Tools\ffmpeg\ffprobe.exe",
            @"C:\Tools\deno\deno.exe",
            "2026.10.26", "8.1", "2.9.8");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void DiskSpaceLedger_BlocksOverCommittedConcurrentJobsAndReleasesCleanly()
    {
        // Simulate 3.0 GB free on C:\
        const long freeC = 3L * 1024 * 1024 * 1024;
        var ledger = new DiskSpaceReservationLedger(drive => drive.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) ? freeC : null);

        // Each 600 MB job on same drive reserves 2.2 * 600 MB = 1.32 GB (+ 200 MB OS margin -> 1.52 GB for 1st, 2.84 GB for 2nd)
        var est600Mb = 600L * 1024 * 1024;
        Assert.True(ledger.TryReserve("j1", @"C:\Temp\j1", @"C:\Videos", est600Mb, isLivestream: false).Succeeded);
        Assert.True(ledger.TryReserve("j2", @"C:\Temp\j2", @"C:\Videos", est600Mb, isLivestream: false).Succeeded);

        // 3rd job pushes required to ~4.16 GB > 3.0 GB -> must fail with clear required vs available message
        var overCommit = ledger.TryReserve("j3", @"C:\Temp\j3", @"C:\Videos", est600Mb, isLivestream: false);
        Assert.False(overCommit.Succeeded);
        Assert.Contains(@"C:\", overCommit.FailureMessage);
        Assert.Contains("Required:", overCommit.FailureMessage);
        Assert.Contains("Available:", overCommit.FailureMessage);

        // Release j1 -> j3 now fits cleanly
        ledger.Release("j1");
        Assert.True(ledger.TryReserve("j3", @"C:\Temp\j3", @"C:\Videos", est600Mb, isLivestream: false).Succeeded);
    }

    [Fact]
    public void DiskSpaceLedger_TracksSeparateTempAndDestinationVolumesIndependently()
    {
        var freeByDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] = 3L * 1024 * 1024 * 1024, // 3 GB temp drive
            [@"D:\"] = 1L * 1024 * 1024 * 1024  // 1 GB save drive
        };
        var ledger = new DiskSpaceReservationLedger(drive => freeByDrive.TryGetValue(drive, out var free) ? free : null);

        // 900 MB job: needs 1.8 GB + 200 MB on C:\ (fits), needs 1.035 GB + 200 MB = 1.235 GB on D:\ (exceeds 1 GB!)
        var est900Mb = 900L * 1024 * 1024;
        var res = ledger.TryReserve("j1", @"C:\Temp\j1", @"D:\Videos", est900Mb, isLivestream: false);
        Assert.False(res.Succeeded);
        Assert.Equal(@"D:\", res.DriveRoot);
        Assert.Equal(0, ledger.GetReservedBytesForDrive(@"C:\")); // Failed reservation must not leak onto C:\
    }

    [Fact]
    public void DiskSpaceLedger_AppliesFallbackFloorForUnknownVodAndLivestreamSizes()
    {
        // Give 10 GB free on C:\
        var ledger = new DiskSpaceReservationLedger(_ => 10L * 1024 * 1024 * 1024);

        // Unknown VOD -> 512 MB * 2.2
        Assert.True(ledger.TryReserve("vod-unknown", @"C:\Temp\v", @"C:\Videos", estimatedSizeBytes: null, isLivestream: false).Succeeded);
        var vodReserved = ledger.GetReservedBytesForDrive(@"C:\");
        Assert.Equal((long)Math.Ceiling(DiskSpaceReservationLedger.DefaultUnknownVodEstimateBytes * 2.2), vodReserved);

        ledger.Release("vod-unknown");

        // Unknown Livestream -> 2 GB * 2.2
        Assert.True(ledger.TryReserve("live-unknown", @"C:\Temp\l", @"C:\Videos", estimatedSizeBytes: 0, isLivestream: true).Succeeded);
        var liveReserved = ledger.GetReservedBytesForDrive(@"C:\");
        Assert.Equal((long)Math.Ceiling(DiskSpaceReservationLedger.DefaultUnknownLivestreamEstimateBytes * 2.2), liveReserved);
    }

    [Fact]
    public void ErrorClassifier_ProducesStructuredRetryDirectivesForTransient403AndPermanent()
    {
        // Transient network / 5xx -> ExponentialBackoff max 3 (5s -> 15s -> 45s)
        var net = YtDlpErrorClassifier.Classify("ERROR: Unable to download webpage: HTTP Error 503: Service Unavailable", 1);
        Assert.Equal(DownloadErrorCategory.TransientNetwork, net.Category);
        Assert.Equal(RetryStrategy.ExponentialBackoff, net.EffectiveRetry.Strategy);
        Assert.Equal(3, net.EffectiveRetry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), net.EffectiveRetry.ComputeDelay(1, jitterMultiplierOverride: 1.0));
        Assert.Equal(TimeSpan.FromSeconds(15), net.EffectiveRetry.ComputeDelay(2, jitterMultiplierOverride: 1.0));
        Assert.Equal(TimeSpan.FromSeconds(45), net.EffectiveRetry.ComputeDelay(3, jitterMultiplierOverride: 1.0));

        // Rate limited (429) -> ExponentialBackoff max 3
        var rate = YtDlpErrorClassifier.Classify("ERROR: HTTP Error 429: Too Many Requests", 1);
        Assert.Equal(DownloadErrorCategory.RateLimited, rate.Category);
        Assert.Equal(RetryStrategy.ExponentialBackoff, rate.EffectiveRetry.Strategy);
        Assert.Equal(3, rate.EffectiveRetry.MaxAttempts);

        // HTTP 403 / expired manifest -> ReprobeAndRetry max 2
        var forbidden = YtDlpErrorClassifier.Classify("ERROR: unable to download video data: HTTP Error 403: Forbidden", 1);
        Assert.Equal(DownloadErrorCategory.ExpiredStreamOrForbidden, forbidden.Category);
        Assert.Equal(RetryStrategy.ReprobeAndRetry, forbidden.EffectiveRetry.Strategy);
        Assert.Equal(2, forbidden.EffectiveRetry.MaxAttempts);

        // Permanent errors -> fail fast (0 retries)
        foreach (var permanentErr in new[]
        {
            "ERROR: Private video",
            "ERROR: Video unavailable",
            "ERROR: This video is geo-restricted",
            "ERROR: disabled DVR",
            "ERROR: 'bad-url' is not a valid URL"
        })
        {
            var classified = YtDlpErrorClassifier.Classify(permanentErr, 1);
            Assert.Equal(DownloadErrorCategory.Permanent, classified.Category);
            Assert.Equal(RetryStrategy.None, classified.EffectiveRetry.Strategy);
            Assert.Equal(0, classified.EffectiveRetry.MaxAttempts);
        }
    }

    [Fact]
    public void ArgumentBuilder_EmitsLimitRateAndContinueFlagsCorrectly()
    {
        var quality = new QualityOption("video-1080", "1080p", false, 1080, 30, "H.264", null, "bv*+ba/b", null, true, ["mp4", "mkv"]);
        var req = new DownloadJobRequest("j1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Test", "Chan", null, _tempDir, quality, "mp4", MediaLiveStatus.NotLive);
        var tempJobDir = Path.Combine(_tempDir, "j1");

        var unlimitedArgs = YtDlpArgumentBuilder.BuildStandardDownloadArguments(_toolchain, req, _tempDir, tempJobDir, "Test [dQw4w9WgXcQ]", perDownloadSpeedLimitKbps: 0, resumeExisting: false);
        Assert.DoesNotContain("--limit-rate", unlimitedArgs);
        Assert.DoesNotContain("--continue", unlimitedArgs);

        var cappedResumedArgs = YtDlpArgumentBuilder.BuildStandardDownloadArguments(_toolchain, req, _tempDir, tempJobDir, "Test [dQw4w9WgXcQ]", perDownloadSpeedLimitKbps: 5120, resumeExisting: true);
        var limitIdx = cappedResumedArgs.ToList().IndexOf("--limit-rate");
        Assert.True(limitIdx >= 0);
        Assert.Equal("5120K", cappedResumedArgs[limitIdx + 1]);
        Assert.Contains("--continue", cappedResumedArgs);

        var liveArgs = YtDlpArgumentBuilder.BuildLivestreamArguments(_toolchain, req, _tempDir, tempJobDir, "Live [dQw4w9WgXcQ]", LivestreamRecordingMode.RecordFromNow, perDownloadSpeedLimitKbps: 2048);
        var liveLimitIdx = liveArgs.ToList().IndexOf("--limit-rate");
        Assert.True(liveLimitIdx >= 0);
        Assert.Equal("2048K", liveArgs[liveLimitIdx + 1]);
    }

    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("audio.m4a", true)]
    [InlineData("clip.mov", true)]
    [InlineData("clip.mkv", false)]
    [InlineData("clip.webm", false)]
    [InlineData("track.mp3", false)]
    [InlineData("track.opus", false)]
    public void MediaIntegrityService_RepairArguments_OnlyIncludeFastStartForMp4M4aMov(string outputFileName, bool expectFastStart)
    {
        var args = MediaIntegrityService.BuildLosslessRepairArguments(@"C:\in.dat", Path.Combine(_tempDir, outputFileName));

        if (expectFastStart)
        {
            Assert.Contains("-movflags", args);
            Assert.Contains("+faststart", args);
        }
        else
        {
            Assert.DoesNotContain("-movflags", args);
            Assert.DoesNotContain("+faststart", args);
        }
    }

    [Fact]
    public void MediaIntegrityService_ParseFfprobeJson_IgnoresAttachedPicCoverArtStream()
    {
        const string mp3WithCoverArtJson = """
        {
          "format": { "duration": "214.52" },
          "streams": [
            { "codec_type": "audio", "codec_name": "mp3" },
            { "codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500, "disposition": { "attached_pic": 1 } }
          ]
        }
        """;

        // Audio-only file with cover art must report HasVideoStream == false, HasAudioStream == true, IsValid == true
        var audioCheck = MediaIntegrityService.ParseFfprobeJson(mp3WithCoverArtJson, @"C:\Music\song.mp3", 4_500_000, expectVideo: false, expectAudio: true);
        Assert.True(audioCheck.IsValid);
        Assert.False(audioCheck.HasVideoStream);
        Assert.True(audioCheck.HasAudioStream);
        Assert.Null(audioCheck.Resolution);
        Assert.Equal("mp3", audioCheck.AudioCodec);

        // If a video job somehow only had audio + embedded thumbnail, expectVideo: true MUST fail!
        var videoCheck = MediaIntegrityService.ParseFfprobeJson(mp3WithCoverArtJson, @"C:\Videos\broken.mp4", 4_500_000, expectVideo: true, expectAudio: true);
        Assert.False(videoCheck.IsValid);
        Assert.Contains("missing expected video stream", videoCheck.IssueSummary);
    }

    [Fact]
    public async Task MediaIntegrityService_VerifyAndRepair_RepairsBrokenDurationAndReplacesAtomically()
    {
        var targetPath = Path.Combine(_tempDir, "broken.mp4");
        await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4, 5]);

        var probeCalls = 0;
        var ffmpegCalled = false;
        IReadOnlyList<string>? capturedFfmpegArgs = null;

        var fakeRunner = new FakeIntegrityProcessRunner(req =>
        {
            if (req.ExecutablePath.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                probeCalls++;
                if (probeCalls == 1)
                {
                    // Initial probe: missing duration -> triggers repair
                    const string badJson = """
                    {
                      "format": { "duration": "0.0" },
                      "streams": [
                        { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
                        { "codec_type": "audio", "codec_name": "aac" }
                      ]
                    }
                    """;
                    return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), badJson, string.Empty);
                }

                // Second probe on repaired file: healthy!
                const string goodJson = """
                {
                  "format": { "duration": "182.4" },
                  "streams": [
                    { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
                    { "codec_type": "audio", "codec_name": "aac" }
                  ]
                }
                """;
                return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(10), goodJson, string.Empty);
            }

            // FFmpeg repair step: write valid bytes to output path (last argument)
            ffmpegCalled = true;
            capturedFfmpegArgs = req.Arguments;
            var outPath = req.Arguments[^1];
            File.WriteAllBytes(outPath, [10, 20, 30, 40, 50, 60, 70, 80]);
            return new ProcessRunResult(0, false, false, false, TimeSpan.FromMilliseconds(20), string.Empty, string.Empty);
        });

        var svc = new MediaIntegrityService(fakeRunner, NullLogger<MediaIntegrityService>.Instance);
        var result = await svc.VerifyAndRepairIfNeededAsync(_toolchain, targetPath, expectVideo: true, expectAudio: true, workingTempDirectory: _tempDir);

        Assert.True(ffmpegCalled);
        Assert.NotNull(capturedFfmpegArgs);
        Assert.Contains("-movflags", capturedFfmpegArgs);
        Assert.Equal(MediaIntegrityStatus.Repaired, result.Status);
        Assert.True(result.WasRepaired);
        Assert.Equal("1920x1080", result.Resolution);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal(182.4, result.DurationSeconds);
        Assert.Equal(8, new FileInfo(targetPath).Length); // Original replaced atomically
    }

    [Fact]
    public async Task MediaIntegrityService_WhenRepairFails_PreservesOriginalAndFlagsNeedsAttention()
    {
        var targetPath = Path.Combine(_tempDir, "corrupt.mkv");
        byte[] originalBytes = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(targetPath, originalBytes);

        var fakeRunner = new FakeIntegrityProcessRunner(req =>
        {
            if (req.ExecutablePath.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                return new ProcessRunResult(1, false, false, false, TimeSpan.FromMilliseconds(5), string.Empty, "Invalid data found when processing input");

            // FFmpeg repair fails too
            Assert.DoesNotContain("-movflags", req.Arguments);
            return new ProcessRunResult(1, false, false, false, TimeSpan.FromMilliseconds(5), string.Empty, "moov atom not found");
        });

        var svc = new MediaIntegrityService(fakeRunner, NullLogger<MediaIntegrityService>.Instance);
        var result = await svc.VerifyAndRepairIfNeededAsync(_toolchain, targetPath, expectVideo: true, expectAudio: true);

        Assert.Equal(MediaIntegrityStatus.NeedsAttention, result.Status);
        Assert.False(result.WasRepaired);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(targetPath));
    }

    private sealed class FakeIntegrityProcessRunner(Func<ProcessRunRequest, ProcessRunResult> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request));

        public void Dispose() { }
    }
}
