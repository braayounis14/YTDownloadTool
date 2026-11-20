using System.Text.RegularExpressions;

namespace YTDownloadTool.Core.Logging;

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"(?i)(authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*[^\r\n;""']+", RegexOptions.Compiled)]
    private static partial Regex HeaderSecretRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|key|sig|signature|access_token|auth|po_token)=)[^&\s""']+", RegexOptions.Compiled)]
    private static partial Regex QuerySecretRegex();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var step1 = HeaderSecretRegex().Replace(input, "$1: [REDACTED]");
        return QuerySecretRegex().Replace(step1, "$1[REDACTED]");
    }
}
