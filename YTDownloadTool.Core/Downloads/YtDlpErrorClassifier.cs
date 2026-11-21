namespace YTDownloadTool.Core.Downloads;

public enum DownloadErrorCategory
{
    Permanent = 0,
    TransientNetwork = 1,
    RateLimited = 2,
    ExpiredStreamOrForbidden = 3,
    DiskOrPermission = 4,
    ToolMissingOrBroken = 5,
    Unknown = 6
}

public enum RetryStrategy
{
    None = 0,
    ExponentialBackoff = 1,
    ReprobeAndRetry = 2
}

public sealed record RetryDirective(
    RetryStrategy Strategy,
    int MaxAttempts,
    IReadOnlyList<TimeSpan> BaseDelays)
{
    public static readonly RetryDirective NoRetry = new(RetryStrategy.None, 0, []);

    public static readonly RetryDirective TransientBackoff = new(
        RetryStrategy.ExponentialBackoff,
        MaxAttempts: 3,
        BaseDelays:
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(45)
        ]);

    public static readonly RetryDirective ReprobeStream = new(
        RetryStrategy.ReprobeAndRetry,
        MaxAttempts: 2,
        BaseDelays:
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(6)
        ]);

    public bool ShouldRetry => Strategy != RetryStrategy.None && MaxAttempts > 0;

    /// <summary>
    /// Computes the retry delay for a 1-based attempt number with ±20% jitter.
    /// </summary>
    public TimeSpan ComputeDelay(int attemptNumber, double? jitterMultiplierOverride = null)
    {
        if (BaseDelays.Count == 0 || attemptNumber <= 0)
            return TimeSpan.Zero;

        var idx = Math.Clamp(attemptNumber - 1, 0, BaseDelays.Count - 1);
        var baseSeconds = BaseDelays[idx].TotalSeconds;

        // Apply ±20% jitter unless overridden deterministically by tests
        var jitter = jitterMultiplierOverride ?? (0.8 + Random.Shared.NextDouble() * 0.4);
        var effectiveSeconds = Math.Max(0.1, baseSeconds * jitter);
        return TimeSpan.FromSeconds(effectiveSeconds);
    }
}

public sealed record ClassifiedDownloadError(
    string UserMessage,
    string TechnicalSummary,
    bool SuggestToolUpdate,
    bool IsTransientNetworkError,
    DownloadErrorCategory Category = DownloadErrorCategory.Unknown,
    RetryDirective? Retry = null)
{
    public RetryDirective EffectiveRetry => Retry ?? RetryDirective.NoRetry;
}

public static class YtDlpErrorClassifier
{
    public static ClassifiedDownloadError Classify(string stdErr, int exitCode)
    {
        var text = stdErr ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var condensedError = ExtractPrimaryErrorLine(text);

        // --- Permanent errors: fail fast (0 retries) ---
        if (lower.Contains("private video"))
        {
            return new(
                "This video is private and cannot be downloaded without access.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("video unavailable")
            || lower.Contains("this video is not available")
            || lower.Contains("has been removed")
            || lower.Contains("account associated with this video has been terminated"))
        {
            return new(
                "This video is unavailable or has been removed by the uploader.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("sign in to confirm your age") || lower.Contains("age-restricted"))
        {
            return new(
                "This video is age-restricted by YouTube and requires sign-in.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("members-only") || lower.Contains("join this channel"))
        {
            return new(
                "This video is restricted to channel members only.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("not made this video available in your country") || lower.Contains("geo-restricted"))
        {
            return new(
                "This video is geo-restricted and not available in your region.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("live event will begin") || lower.Contains("premieres in"))
        {
            return new(
                "This livestream has not started yet. Wait until it goes live before recording.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("disabled dvr") || lower.Contains("cannot download from start"))
        {
            return new(
                "The broadcaster disabled DVR/rewind for this livestream. Switch to 'Record from now' (Mode A).",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        if (lower.Contains("is not a valid url")
            || lower.Contains("unsupported url")
            || lower.Contains("incomplete youtube id"))
        {
            return new(
                "The provided URL is invalid or not a supported YouTube video/livestream.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        // --- Engine/tool errors: fail fast ---
        if (lower.Contains("no supported javascript runtime"))
        {
            return new(
                "The bundled JavaScript engine (Deno) could not run. Open Settings → Engine and click Repair on Deno.",
                condensedError, true, false,
                DownloadErrorCategory.ToolMissingOrBroken, RetryDirective.NoRetry);
        }

        if (lower.Contains("ffmpeg is not installed") || lower.Contains("ffprobe and ffmpeg not found"))
        {
            return new(
                "FFmpeg could not be found. Open Settings → Engine and click Repair on FFmpeg.",
                condensedError, true, false,
                DownloadErrorCategory.ToolMissingOrBroken, RetryDirective.NoRetry);
        }

        // --- Disk / filesystem errors: fail fast ---
        if (lower.Contains("no space left on device") || lower.Contains("there is not enough space on the disk"))
        {
            return new(
                "Not enough free disk space in the selected download folder.",
                condensedError, false, false,
                DownloadErrorCategory.DiskOrPermission, RetryDirective.NoRetry);
        }

        if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("unable to open for writing"))
        {
            return new(
                "Cannot write to the selected download folder. Check folder permissions or choose a different folder.",
                condensedError, false, false,
                DownloadErrorCategory.DiskOrPermission, RetryDirective.NoRetry);
        }

        if (lower.Contains("requested format is not available"))
        {
            return new(
                "The selected quality is no longer available for this stream. Re-analyze the URL and pick another available quality.",
                condensedError, false, false,
                DownloadErrorCategory.Permanent, RetryDirective.NoRetry);
        }

        // --- HTTP 403 / expired signed URL / expired live manifest -> re-probe before retrying (max 2) ---
        if (lower.Contains("http error 403")
            || lower.Contains("http error 410")
            || lower.Contains("nsig extraction failed")
            || lower.Contains("unable to extract")
            || lower.Contains("url has expired")
            || lower.Contains("expired manifest")
            || lower.Contains("live manifest"))
        {
            return new(
                "YouTube rejected the stream URL or the signed link expired. Retrying after refreshing stream metadata; if it persists, update yt-dlp in Settings → Engine.",
                condensedError, true, false,
                DownloadErrorCategory.ExpiredStreamOrForbidden, RetryDirective.ReprobeStream);
        }

        // --- Rate-limiting (HTTP 429) -> exponential backoff with jitter (max 3) ---
        if (lower.Contains("http error 429")
            || lower.Contains("too many requests")
            || lower.Contains("sign in to confirm you’re not a bot")
            || lower.Contains("sign in to confirm you're not a bot")
            || lower.Contains("not a bot"))
        {
            return new(
                "YouTube temporarily rate-limited requests from this network. Retrying with backoff; if it persists, wait a few minutes or update yt-dlp in Settings → Engine.",
                condensedError, true, true,
                DownloadErrorCategory.RateLimited, RetryDirective.TransientBackoff);
        }

        // --- Offline / DNS / unreachable network errors ---
        if (lower.Contains("failed to resolve")
            || lower.Contains("temporary failure in name resolution")
            || lower.Contains("getaddrinfo failed")
            || lower.Contains("nodename nor servname")
            || lower.Contains("name or service not known")
            || lower.Contains("no such host is known")
            || lower.Contains("network is unreachable")
            || lower.Contains("no route to host")
            || lower.Contains("urlopen error"))
        {
            return new(
                "No internet connection detected or the media server could not be reached. Check your network connection and try again.",
                condensedError, false, true,
                DownloadErrorCategory.TransientNetwork, RetryDirective.TransientBackoff);
        }

        // --- Transient network / HTTP 5xx / timeout errors -> exponential backoff with jitter (max 3) ---
        if (lower.Contains("timed out")
            || lower.Contains("timeout")
            || lower.Contains("connection reset")
            || lower.Contains("connection aborted")
            || lower.Contains("remote end closed")
            || lower.Contains("broken pipe")
            || lower.Contains("unable to download webpage")
            || lower.Contains("incomplete read")
            || lower.Contains("http error 500")
            || lower.Contains("http error 502")
            || lower.Contains("http error 503")
            || lower.Contains("http error 504")
            || lower.Contains("service unavailable")
            || lower.Contains("bad gateway")
            || lower.Contains("internal server error")
            || lower.Contains("socket error")
            || lower.Contains("eof occurred in violation of protocol"))
        {
            return new(
                "Network connection failed or timed out while reaching the media server. Check your internet connection and retry.",
                condensedError, false, true,
                DownloadErrorCategory.TransientNetwork, RetryDirective.TransientBackoff);
        }

        return new(
            UserMessage: string.IsNullOrWhiteSpace(condensedError)
                ? $"Download failed unexpectedly (exit code {exitCode}). Check diagnostics for details."
                : $"Download failed: {condensedError}",
            TechnicalSummary: string.IsNullOrWhiteSpace(condensedError) ? $"Exit code {exitCode}" : condensedError,
            SuggestToolUpdate: false,
            IsTransientNetworkError: false,
            Category: DownloadErrorCategory.Unknown,
            Retry: RetryDirective.NoRetry);
    }

    public static string ExtractPrimaryErrorLine(string stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr))
            return string.Empty;

        var lines = stdErr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var explicitError = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(explicitError))
            return explicitError["ERROR:".Length..].Trim();

        return lines.LastOrDefault() ?? string.Empty;
    }
}
