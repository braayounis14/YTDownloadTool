namespace YTDownloadTool.Core.Downloads;

public sealed record ClassifiedDownloadError(
    string UserMessage,
    string TechnicalSummary,
    bool SuggestToolUpdate,
    bool IsTransientNetworkError);

public static class YtDlpErrorClassifier
{
    public static ClassifiedDownloadError Classify(string stdErr, int exitCode)
    {
        var text = stdErr ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var condensedError = ExtractPrimaryErrorLine(text);

        if (lower.Contains("private video"))
            return new("This video is private and cannot be downloaded without access.", condensedError, false, false);

        if (lower.Contains("video unavailable") || lower.Contains("this video is not available") || lower.Contains("has been removed"))
            return new("This video is unavailable or has been removed by the uploader.", condensedError, false, false);

        if (lower.Contains("sign in to confirm your age") || lower.Contains("age-restricted"))
            return new("This video is age-restricted by YouTube and requires sign-in.", condensedError, false, false);

        if (lower.Contains("members-only") || lower.Contains("join this channel"))
            return new("This video is restricted to channel members only.", condensedError, false, false);

        if (lower.Contains("not made this video available in your country") || lower.Contains("geo-restricted"))
            return new("This video is geo-restricted and not available in your region.", condensedError, false, false);

        if (lower.Contains("live event will begin") || lower.Contains("premieres in"))
            return new("This livestream has not started yet. Wait until it goes live before recording.", condensedError, false, false);

        if (lower.Contains("disabled dvr") || lower.Contains("cannot download from start"))
            return new("The broadcaster disabled DVR/rewind for this livestream. Switch to 'Record from now' (Mode A).", condensedError, false, false);

        if (lower.Contains("no supported javascript runtime"))
            return new("The bundled JavaScript engine (Deno) could not run. Open Settings → Engine and click Repair on Deno.", condensedError, true, false);

        if (lower.Contains("ffmpeg is not installed") || lower.Contains("ffprobe and ffmpeg not found"))
            return new("FFmpeg could not be found. Open Settings → Engine and click Repair on FFmpeg.", condensedError, true, false);

        if (lower.Contains("requested format is not available"))
            return new("The selected quality is no longer available for this stream. Re-analyze the URL and pick another available quality.", condensedError, false, false);

        if (lower.Contains("no space left on device") || lower.Contains("there is not enough space on the disk"))
            return new("Not enough free disk space in the selected download folder.", condensedError, false, false);

        if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("unable to open for writing"))
            return new("Cannot write to the selected download folder. Check folder permissions or choose a different folder.", condensedError, false, false);

        if (lower.Contains("http error 429") || lower.Contains("too many requests") || lower.Contains("sign in to confirm you’re not a bot") || lower.Contains("not a bot"))
            return new("YouTube temporarily rate-limited requests from this network. Wait a few minutes or update yt-dlp in Settings → Engine.", condensedError, true, true);

        if (lower.Contains("http error 403") || lower.Contains("nsig extraction failed") || lower.Contains("unable to extract"))
            return new("YouTube rejected the download request or changed its page structure. Updating yt-dlp in Settings → Engine usually resolves this.", condensedError, true, false);

        if (lower.Contains("timed out") || lower.Contains("temporary failure in name resolution") || lower.Contains("getaddrinfo failed") || lower.Contains("connection reset") || lower.Contains("unable to download webpage"))
            return new("Network connection failed or timed out while reaching YouTube. Check your internet connection and retry.", condensedError, false, true);

        return new(
            UserMessage: string.IsNullOrWhiteSpace(condensedError)
                ? $"Download failed unexpectedly (exit code {exitCode}). Check diagnostics for details."
                : $"Download failed: {condensedError}",
            TechnicalSummary: string.IsNullOrWhiteSpace(condensedError) ? $"Exit code {exitCode}" : condensedError,
            SuggestToolUpdate: false,
            IsTransientNetworkError: false);
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
