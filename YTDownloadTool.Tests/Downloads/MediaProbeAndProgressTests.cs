using System.Globalization;
using Xunit;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Downloads;

public sealed class MediaProbeAndProgressTests : IDisposable
{
    private readonly string _tempDir;

    public MediaProbeAndProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ytdl_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ParseProbeJson_BuildsDynamicQualitiesWithoutHardcodingMissingResolutions()
    {
        const string sampleJson = """
        {
          "id": "abc12345678",
          "webpage_url": "https://www.youtube.com/watch?v=abc12345678",
          "title": "Sample 4K & 1080p60 Test",
          "channel": "Test Channel",
          "duration": 600,
          "live_status": "not_live",
          "thumbnails": [
            { "url": "https://i.ytimg.com/vi/abc12345678/hqdefault.jpg", "width": 480 },
            { "url": "https://i.ytimg.com/vi/abc12345678/sddefault.jpg", "width": 640 }
          ],
          "formats": [
            { "format_id": "sb0", "ext": "mhtml", "protocol": "mhtml" },
            { "format_id": "251", "ext": "webm", "protocol": "https", "vcodec": "none", "acodec": "opus", "tbr": 128, "filesize": 9600000 },
            { "format_id": "140", "ext": "m4a", "protocol": "https", "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 129, "filesize": 9700000 },
            { "format_id": "18", "ext": "mp4", "protocol": "https", "width": 640, "height": 360, "fps": 30, "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "filesize": 35000000 },
            { "format_id": "299", "ext": "mp4", "protocol": "https", "width": 1920, "height": 1080, "fps": 60, "vcodec": "avc1.64002a", "acodec": "none", "filesize": 180000000 },
            { "format_id": "315", "ext": "webm", "protocol": "https", "width": 3840, "height": 2160, "fps": 60, "vcodec": "vp9", "acodec": "none", "dynamic_range": "HDR10", "filesize": 550000000 }
          ]
        }
        """;

        var result = MediaProbeService.ParseProbeJson(sampleJson, "https://www.youtube.com/watch?v=abc12345678");

        Assert.Equal("abc12345678", result.VideoId);
        Assert.Equal(MediaLiveStatus.NotLive, result.LiveStatus);
        Assert.Equal("https://i.ytimg.com/vi/abc12345678/sddefault.jpg", result.ThumbnailUrl);

        // Must contain Best, 2160p, 1080p, 360p, plus Audio Only options — and MUST NOT invent 720p or 1440p!
        Assert.Contains(result.AvailableQualities, q => q.Height == 2160 && q.Label.Contains("HDR"));
        Assert.Contains(result.AvailableQualities, q => q.Height == 1080 && q.Fps == 60);
        Assert.Contains(result.AvailableQualities, q => q.Height == 360);
        Assert.DoesNotContain(result.AvailableQualities, q => q.Height == 720);
        Assert.DoesNotContain(result.AvailableQualities, q => q.Height == 1440);
        Assert.Contains(result.AvailableQualities, q => q.IsAudioOnly && q.PreferredAudioExtension == "mp3");
        Assert.Contains(result.AvailableQualities, q => q.IsAudioOnly && q.PreferredAudioExtension == "m4a");
    }

    [Fact]
    public void ProgressParser_ParsesInvariantCultureEvenUnderCommaLocale()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal separator ','

            const string line = "YTDL_PROG|downloading|5242880|10485760|NA|2621440.5|12.7|15|100|avc1.64002a|none";
            var parsed = YtDlpProgressParser.ParseLine(line);

            Assert.Equal(ParsedOutputKind.DownloadProgress, parsed.Kind);
            Assert.Equal(5242880, parsed.DownloadedBytes);
            Assert.Equal(10485760, parsed.TotalBytes);
            Assert.Equal(2621440.5, parsed.SpeedBytesPerSecond!.Value, 1);
            Assert.Equal(TimeSpan.FromSeconds(12.7), parsed.Eta);
            Assert.Equal(15, parsed.FragmentIndex);
            Assert.Equal(100, parsed.FragmentCount);
            Assert.True(parsed.HasVideo);
            Assert.False(parsed.HasAudio);

            const string ffmpegLine = "frame=  420 fps=30 q=-1.0 size=   12544KiB time=00:02:15.50 bitrate= 758.2kbits/s speed=1.02x";
            var ffParsed = YtDlpProgressParser.ParseLine(ffmpegLine);
            Assert.Equal(ParsedOutputKind.FFmpegProgress, ffParsed.Kind);
            Assert.Equal(TimeSpan.FromSeconds(135.5), ffParsed.MediaTime);
            Assert.Equal(12544L * 1024, ffParsed.DownloadedBytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void ErrorClassifier_TranslatesKnownYtDlpErrorsToUserFriendlyMessages()
    {
        var priv = YtDlpErrorClassifier.Classify("ERROR: [youtube] xyz: Private video. Sign in if you've been granted access", 1);
        Assert.Contains("private", priv.UserMessage, StringComparison.OrdinalIgnoreCase);

        var dvr = YtDlpErrorClassifier.Classify("WARNING: Downloading from the live edge; the streamer has disabled DVR", 1);
        Assert.Contains("Mode A", dvr.UserMessage);

        var nsig = YtDlpErrorClassifier.Classify("ERROR: [youtube] xyz: nsig extraction failed: HTTP Error 403", 1);
        Assert.True(nsig.SuggestToolUpdate);
    }

    [Fact]
    public void OutputPathGuard_PreventsCollisionsAndSanitizesReservedNames()
    {
        var guard = new OutputPathGuard();
        Assert.Equal("_CON", OutputPathGuard.SanitizeFileNameSegment("CON", 50));
        Assert.Equal("Hello_World_Test", OutputPathGuard.SanitizeFileNameSegment("Hello:World?Test", 50));

        using var lease1 = guard.ReserveUniqueOutputPath(_tempDir, "My Video", "abc12345678", "mp4");
        using var lease2 = guard.ReserveUniqueOutputPath(_tempDir, "My Video", "abc12345678", "mp4");

        Assert.NotEqual(lease1.ReservedBasePath, lease2.ReservedBasePath);
        Assert.Contains("(2)", lease2.TargetFilePath);
    }

    [Fact]
    public void ArgumentBuilder_NeverInjectsBrokenPlayerClientOverrideAndPassesManagedTools()
    {
        var tc = new ResolvedToolchain(
            @"C:\Tools\yt-dlp\yt-dlp.exe",
            @"C:\Tools\ffmpeg",
            @"C:\Tools\ffmpeg\ffmpeg.exe",
            @"C:\Tools\ffmpeg\ffprobe.exe",
            @"C:\Tools\deno\deno.exe",
            "2026.10.26", "8.1", "2.9.8");

        var quality = new QualityOption("video-1080-std", "1080p", false, 1080, 30, "H.264", null, "bv*[height<=1080]+ba/b", null, true, ["mp4", "mkv"]);
        var req = new DownloadJobRequest("j1", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Test", "Chan", null, _tempDir, quality, "mp4", MediaLiveStatus.NotLive);

        var args = YtDlpArgumentBuilder.BuildStandardDownloadArguments(tc, req, Path.Combine(_tempDir, "Test [dQw4w9WgXcQ]"));

        Assert.DoesNotContain(args, a => a.Contains("player-client=ios", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--js-runtimes", args);
        Assert.Contains(@"deno:C:\Tools\deno\deno.exe", args);
        Assert.Contains("--ffmpeg-location", args);
        Assert.Contains(@"C:\Tools\ffmpeg", args);
        Assert.Contains("--", args);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", args[^1]);
    }

    [Fact]
    public void ModeCTargetDuration_CalculatesSafelyAndClampsToDvrWindow()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        // Stream started 10 minutes ago, margin 30s -> 9m30s
        var target = DownloadOrchestrator.CalculateModeCTargetDuration(now.AddMinutes(-10), 30, now);
        Assert.Equal(TimeSpan.FromSeconds(570), target);

        // Stream started 20 seconds ago, margin 30s -> null (too fresh)
        Assert.Null(DownloadOrchestrator.CalculateModeCTargetDuration(now.AddSeconds(-20), 30, now));

        // Stream running for 200 hours -> clamped to 120h - 60s
        var clamped = DownloadOrchestrator.CalculateModeCTargetDuration(now.AddHours(-200), 60, now);
        Assert.Equal(TimeSpan.FromHours(120) - TimeSpan.FromSeconds(60), clamped);
    }
}
