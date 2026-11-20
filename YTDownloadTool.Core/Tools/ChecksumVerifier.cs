using System.Security.Cryptography;

namespace YTDownloadTool.Core.Tools;

public static class ChecksumVerifier
{
    /// <summary>
    /// Extracts the expected SHA-256 hex digest for <paramref name="expectedFileName"/> from
    /// standard checksum files:
    /// - GNU sha256sum format: "&lt;64-hex&gt;  filename" or "&lt;64-hex&gt; *filename"
    /// - Single-hash format (e.g., Deno .sha256sum): "&lt;64-hex&gt;" or "&lt;64-hex&gt;  filename"
    /// </summary>
    public static string? ParseExpectedSha256(string checksumFileContent, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumFileContent))
            return null;

        var lines = checksumFileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // First look for explicit filename match (handles multi-entry SHA2-256SUMS / checksums.sha256).
        foreach (var line in lines)
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsValidSha256Hex(parts[0]))
            {
                var name = parts[^1].TrimStart('*');
                if (name.StartsWith("./", StringComparison.Ordinal))
                    name = name[2..];

                if (string.Equals(name, expectedFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(name), expectedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[0].ToLowerInvariant();
                }
            }
        }

        // PowerShell Format-List output (used by denoland/deno Windows *.sha256sum assets):
        //   Algorithm : SHA256
        //   Hash      : <64-HEX>
        //   Path      : C:\...\deno-x86_64-pc-windows-msvc.zip
        string? psHash = null;
        string? psPath = null;
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line[..colonIdx].Trim();
            var val = line[(colonIdx + 1)..].Trim();
            if (key.Equals("Hash", StringComparison.OrdinalIgnoreCase) && IsValidSha256Hex(val))
            {
                psHash = val.ToLowerInvariant();
            }
            else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                psPath = val;
            }
        }

        if (psHash is not null &&
            (string.IsNullOrWhiteSpace(psPath)
             || Path.GetFileName(psPath).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return psHash;
        }

        // Single-entry checksum file containing only the hash (common for standalone *.sha256sum).
        if (lines.Length == 1 && IsValidSha256Hex(lines[0]))
        {
            return lines[0].ToLowerInvariant();
        }

        return null;
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hashBytes);
    }

    public static async Task<bool> VerifyFileSha256Async(string filePath, string expectedSha256Hex, CancellationToken ct = default)
    {
        if (!IsValidSha256Hex(expectedSha256Hex) || !File.Exists(filePath))
            return false;

        var actual = await ComputeFileSha256Async(filePath, ct);
        var actualBytes = Convert.FromHexString(actual);
        var expectedBytes = Convert.FromHexString(expectedSha256Hex.Trim());
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    public static bool IsValidSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        return true;
    }
}
