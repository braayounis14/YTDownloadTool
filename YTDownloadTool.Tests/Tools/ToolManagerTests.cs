using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Tools;

namespace YTDownloadTool.Tests.Tools;

public sealed class ToolManagerTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly DefaultAppPaths _paths;

    public ToolManagerTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ytdl_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _paths = new DefaultAppPaths(
            appDirectory: Path.Combine(_sandboxRoot, "app"),
            localAppData: Path.Combine(_sandboxRoot, "local"),
            roamingAppData: Path.Combine(_sandboxRoot, "roaming"),
            videosFolder: Path.Combine(_sandboxRoot, "videos"));
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ChecksumVerifier_ParsesGnuAndSingleHashFormats()
    {
        var hash1 = new string('a', 64);
        var hash2 = new string('b', 64);
        var multi = $"""
            {hash1}  yt-dlp.exe
            {hash2} *ffmpeg-master-latest-win64-gpl-shared.zip
            """;

        Assert.Equal(hash1, ChecksumVerifier.ParseExpectedSha256(multi, "yt-dlp.exe"));
        Assert.Equal(hash2, ChecksumVerifier.ParseExpectedSha256(multi, "ffmpeg-master-latest-win64-gpl-shared.zip"));
        Assert.Null(ChecksumVerifier.ParseExpectedSha256(multi, "missing.exe"));
        Assert.Equal(hash1, ChecksumVerifier.ParseExpectedSha256(hash1 + "\n", "deno-x86_64-pc-windows-msvc.zip"));
    }

    [Fact]
    public void SafeZipExtractor_BlocksZipSlipTraversal()
    {
        var zipPath = Path.Combine(_sandboxRoot, "evil.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escaped.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious");
        }

        var extractDir = Path.Combine(_sandboxRoot, "extract");
        Assert.Throws<InvalidDataException>(() =>
            SafeZipExtractor.ExtractFiltered(zipPath, extractDir, e => e.FullName));
    }

    [Fact]
    public void SafeZipExtractor_FFmpegFilterExtractsOnlyBinAndLicense()
    {
        var zipPath = Path.Combine(_sandboxRoot, "ffmpeg.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "ffmpeg-master/bin/ffmpeg.exe", "ffmpeg");
            WriteEntry(archive, "ffmpeg-master/bin/ffprobe.exe", "ffprobe");
            WriteEntry(archive, "ffmpeg-master/bin/ffplay.exe", "ffplay-should-skip");
            WriteEntry(archive, "ffmpeg-master/bin/avcodec-62.dll", "dll");
            WriteEntry(archive, "ffmpeg-master/include/libavcodec.h", "header-skip");
            WriteEntry(archive, "ffmpeg-master/LICENSE.txt", "GPLv3");
        }

        var extractDir = Path.Combine(_sandboxRoot, "ffmpeg_out");
        SafeZipExtractor.ExtractFiltered(zipPath, extractDir, SafeZipExtractor.MapFFmpegSharedEntry);

        Assert.True(File.Exists(Path.Combine(extractDir, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(extractDir, "ffprobe.exe")));
        Assert.True(File.Exists(Path.Combine(extractDir, "avcodec-62.dll")));
        Assert.True(File.Exists(Path.Combine(extractDir, "LICENSE.txt")));
        Assert.False(File.Exists(Path.Combine(extractDir, "ffplay.exe")));
        Assert.False(File.Exists(Path.Combine(extractDir, "libavcodec.h")));
    }

    [Fact]
    public async Task ToolManager_SeedsFromBaseline_UpdatesSafely_AndRollsBack()
    {
        // Stage fake baseline binaries
        SeedFakeBaseline("yt-dlp", ("yt-dlp.exe", "ytdlp-v1"));
        SeedFakeBaseline("ffmpeg", ("ffmpeg.exe", "ffmpeg-v1"), ("ffprobe.exe", "ffprobe-v1"), ("LICENSE.txt", "GPLv3"));
        SeedFakeBaseline("deno", ("deno.exe", "deno-v1"));

        var newYtDlpBytes = Encoding.UTF8.GetBytes("ytdlp-v2-binary");
        var newYtDlpHash = Convert.ToHexStringLower(SHA256.HashData(newYtDlpBytes));

        var fakeProcessRunner = new FakeToolProcessRunner(exePath =>
        {
            var content = File.ReadAllText(exePath);
            return content switch
            {
                "ytdlp-v1" => (0, "2026.10.01"),
                "ytdlp-v2-binary" => (0, "2026.10.26"),
                "ffmpeg-v1" => (0, "ffmpeg version N-12345-gabcdef Copyright (c) FFmpeg"),
                "ffprobe-v1" => (0, "ffprobe version N-12345-gabcdef"),
                "deno-v1" => (0, "deno 2.9.8 (stable, release, x86_64-pc-windows-msvc)"),
                _ => (1, string.Empty)
            };
        });

        var httpHandler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://github.com/yt-dlp/yt-dlp/releases/latest")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26")
                };
                return resp;
            }

            if (url == "https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/SHA2-256SUMS")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{newYtDlpHash}  yt-dlp.exe")
                };
            }

            if (url == "https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(newYtDlpBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var settingsStore = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        using var httpClient = new HttpClient(httpHandler);
        var manager = new ToolManager(_paths, settingsStore, fakeProcessRunner, httpClient, NullLogger<ToolManager>.Instance);

        // 1. EnsureToolsReady seeds all 3 from BaselineTools without network calls
        var toolchain = await manager.EnsureToolsReadyAsync();
        Assert.Equal("2026.10.01", toolchain.YtDlpVersion);
        Assert.Equal("2.9.8", toolchain.DenoVersion);
        Assert.True(File.Exists(toolchain.FFmpegPath));
        Assert.True(File.Exists(toolchain.FFprobePath));

        // 2. Check & apply yt-dlp update atomically
        var check = await manager.CheckForUpdateAsync(ToolKind.YtDlp, force: true);
        Assert.True(check.UpdateAvailable);
        Assert.Equal("2026.10.26", check.LatestVersion);

        var updatedStatus = await manager.UpdateToolAsync(ToolKind.YtDlp);
        Assert.Equal("2026.10.26", updatedStatus.InstalledVersion);
        Assert.True(updatedStatus.CanRollback);
        Assert.Equal("2026.10.01", updatedStatus.PreviousVersion);

        // 3. Roll back to previous version cleanly
        var rolledBack = await manager.RollbackToolAsync(ToolKind.YtDlp);
        Assert.Equal("2026.10.01", rolledBack.InstalledVersion);
    }

    [Fact]
    public async Task ToolManager_CorruptedChecksum_RejectsUpdateAndPreservesWorkingTool()
    {
        SeedFakeBaseline("yt-dlp", ("yt-dlp.exe", "ytdlp-v1"));
        SeedFakeBaseline("ffmpeg", ("ffmpeg.exe", "ffmpeg-v1"), ("ffprobe.exe", "ffprobe-v1"));
        SeedFakeBaseline("deno", ("deno.exe", "deno-v1"));

        var fakeProcessRunner = new FakeToolProcessRunner(exePath =>
        {
            var content = File.ReadAllText(exePath);
            return content switch
            {
                "ytdlp-v1" => (0, "2026.10.01"),
                "ffmpeg-v1" => (0, "ffmpeg version 8.0.1"),
                "ffprobe-v1" => (0, "ffprobe version 8.0.1"),
                "deno-v1" => (0, "deno 2.9.8"),
                _ => (1, string.Empty)
            };
        });

        var wrongHash = new string('f', 64);
        var httpHandler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://github.com/yt-dlp/yt-dlp/releases/latest")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26")
                };
            }

            if (url.EndsWith("SHA2-256SUMS"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{wrongHash}  yt-dlp.exe") };

            if (url.EndsWith("yt-dlp.exe"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("tampered-bytes"u8.ToArray()) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var settingsStore = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        using var httpClient = new HttpClient(httpHandler);
        var manager = new ToolManager(_paths, settingsStore, fakeProcessRunner, httpClient, NullLogger<ToolManager>.Instance);

        var initial = await manager.EnsureToolsReadyAsync();
        Assert.Equal("2026.10.01", initial.YtDlpVersion);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.UpdateToolAsync(ToolKind.YtDlp));

        // Working installation must remain intact!
        var afterFailedUpdate = await manager.EnsureToolsReadyAsync();
        Assert.Equal("2026.10.01", afterFailedUpdate.YtDlpVersion);
        Assert.True(File.Exists(afterFailedUpdate.YtDlpPath));
    }

    [Fact]
    public async Task ToolManager_DeletedActiveBinary_SelfHealsFromBaseline()
    {
        SeedFakeBaseline("yt-dlp", ("yt-dlp.exe", "ytdlp-v1"));
        SeedFakeBaseline("ffmpeg", ("ffmpeg.exe", "ffmpeg-v1"), ("ffprobe.exe", "ffprobe-v1"));
        SeedFakeBaseline("deno", ("deno.exe", "deno-v1"));

        var fakeProcessRunner = new FakeToolProcessRunner(exePath =>
        {
            if (!File.Exists(exePath)) return (1, string.Empty);
            var content = File.ReadAllText(exePath);
            return content switch
            {
                "ytdlp-v1" => (0, "2026.10.01"),
                "ffmpeg-v1" => (0, "ffmpeg version 8.0.1"),
                "ffprobe-v1" => (0, "ffprobe version 8.0.1"),
                "deno-v1" => (0, "deno 2.9.8"),
                _ => (1, string.Empty)
            };
        });

        var settingsStore = new SettingsStore(_paths, NullLogger<SettingsStore>.Instance);
        await settingsStore.LoadAsync();
        using var httpClient = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var manager = new ToolManager(_paths, settingsStore, fakeProcessRunner, httpClient, NullLogger<ToolManager>.Instance);

        var initial = await manager.EnsureToolsReadyAsync();
        File.Delete(initial.YtDlpPath); // Simulate antivirus quarantine / accidental deletion

        var healed = await manager.EnsureToolsReadyAsync();
        Assert.True(File.Exists(healed.YtDlpPath));
        Assert.Equal("2026.10.01", healed.YtDlpVersion);
    }

    private void SeedFakeBaseline(string folder, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_paths.BaselineToolsDirectory, folder);
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class FakeToolProcessRunner(Func<string, (int ExitCode, string StdOut)> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            var (code, stdout) = handler(request.ExecutablePath);
            return Task.FromResult(new ProcessRunResult(code, false, false, false, TimeSpan.FromMilliseconds(5), stdout, string.Empty));
        }

        public void Dispose() { }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
