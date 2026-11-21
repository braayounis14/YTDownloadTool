using System.Text.RegularExpressions;

namespace YTDownloadTool.Core.Logging;

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"(?i)(authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n;""']+", RegexOptions.Compiled)]
    private static partial Regex HeaderSecretRegex();

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|key|sig|signature|access_token|auth|po_token|lsig|lsparams)=)[^&\s""']+", RegexOptions.Compiled)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"(?i)\b((?:__Secure-)?(?:1PSID|3PSID|SAPISID|APISID|HSID|SSID|SID|LOGIN_INFO)=)[^;\s""']+", RegexOptions.Compiled)]
    private static partial Regex CookiePairRegex();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserProfilePathRegex();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var step1 = HeaderSecretRegex().Replace(input, "$1: [REDACTED]");
        var step2 = BearerTokenRegex().Replace(step1, "$1[REDACTED]");
        var step3 = QuerySecretRegex().Replace(step2, "$1[REDACTED]");
        var step4 = CookiePairRegex().Replace(step3, "$1[REDACTED]");
        return WindowsUserProfilePathRegex().Replace(step4, "$1<REDACTED>");
    }
}
