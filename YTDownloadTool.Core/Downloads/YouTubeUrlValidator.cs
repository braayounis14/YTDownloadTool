using System.Text.RegularExpressions;

namespace YTDownloadTool.Core.Downloads;

public sealed record YouTubeUrlValidationResult(
    bool IsValid,
    string? NormalizedUrl,
    string? VideoId,
    bool IsChannelLiveUrl,
    string? ErrorMessage);

public static partial class YouTubeUrlValidator
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be",
        "www.youtu.be"
    };

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"^/(?:shorts|live|embed|v)/([A-Za-z0-9_-]{11})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PathVideoIdRegex();

    [GeneratedRegex(@"^/(?:@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/live/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ChannelLivePathRegex();

    public static YouTubeUrlValidationResult Validate(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Invalid("Please paste a YouTube video or livestream URL.");

        var trimmed = rawInput.Trim();

        // Reject option-like strings or multiline injection attempts immediately.
        if (trimmed.StartsWith('-') || trimmed.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return Invalid("The input is not a valid YouTube URL.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // Support pasted URLs missing scheme (e.g., "youtube.com/watch?v=..." or "youtu.be/...")
            if (!trimmed.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var withScheme))
            {
                uri = withScheme;
            }
            else
            {
                return Invalid("Invalid URL format. Please enter a complete YouTube link.");
            }
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Only HTTP and HTTPS YouTube URLs are supported.");
        }

        if (!AllowedHosts.Contains(uri.Host))
            return Invalid("Only YouTube URLs (youtube.com, youtu.be, music.youtube.com) are supported.");

        // 1. Shortened youtu.be/<id>
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var idSegment = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(idSegment) && VideoIdRegex().IsMatch(idSegment))
            {
                return new YouTubeUrlValidationResult(true, $"https://www.youtube.com/watch?v={idSegment}", idSegment, false, null);
            }

            return Invalid("The shortened youtu.be link does not contain a valid 11-character video ID.");
        }

        // 2. Standard watch?v=<id>
        var queryId = TryExtractQueryParam(uri.Query, "v");
        if (!string.IsNullOrEmpty(queryId))
        {
            if (VideoIdRegex().IsMatch(queryId))
            {
                return new YouTubeUrlValidationResult(true, $"https://www.youtube.com/watch?v={queryId}", queryId, false, null);
            }

            return Invalid("The YouTube video ID in this URL is invalid.");
        }

        // 3. /shorts/<id>, /live/<id>, /embed/<id>, /v/<id>
        var pathMatch = PathVideoIdRegex().Match(uri.AbsolutePath);
        if (pathMatch.Success)
        {
            var id = pathMatch.Groups[1].Value;
            var isLivePath = uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase);
            var normalized = isLivePath
                ? $"https://www.youtube.com/live/{id}"
                : $"https://www.youtube.com/watch?v={id}";

            return new YouTubeUrlValidationResult(true, normalized, id, false, null);
        }

        // 4. Channel live redirect URL (e.g., https://www.youtube.com/@NASA/live)
        if (ChannelLivePathRegex().IsMatch(uri.AbsolutePath))
        {
            var cleanUrl = $"https://www.youtube.com{uri.AbsolutePath.TrimEnd('/')}";
            return new YouTubeUrlValidationResult(true, cleanUrl, null, true, null);
        }

        return Invalid("Please enter a direct YouTube video, Shorts, or livestream URL (playlists and channel home pages are not supported).");
    }

    private static string? TryExtractQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var name = pair[..eqIdx];
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
            }
        }

        return null;
    }

    private static YouTubeUrlValidationResult Invalid(string message) =>
        new(false, null, null, false, message);
}
