using System.Runtime.InteropServices;

namespace YTDownloadTool.Core.Platform;

public static partial class PackageIdentityHelper
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsCompileTimeStoreBuild =>
#if STORE_MSIX
        true;
#else
        false;
#endif

    /// <summary>
    /// Detects at runtime whether the current process is executing with an MSIX package identity
    /// via kernel32!GetCurrentPackageFullName (requires no WinRT/WindowsAppSDK dependency).
    /// </summary>
    public static bool IsPackagedMsix()
    {
        try
        {
            uint length = 0;
            var rc = GetCurrentPackageFullName(ref length, null);
            return rc is ErrorSuccess or ErrorInsufficientBuffer && rc != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the MSIX Package Family Name (e.g. "BaraaYounis.Tidejar_xxxxxxxxxxxxx") when packaged,
    /// or null when running unpackaged (Velopack / portable / dev).
    /// </summary>
    public static string? TryGetPackageFamilyName()
    {
        try
        {
            uint length = 0;
            var rc = GetCurrentPackageFamilyName(ref length, null);
            if (rc != ErrorInsufficientBuffer || length == 0)
                return null;

            var buffer = new char[length];
            rc = GetCurrentPackageFamilyName(ref length, buffer);
            if (rc != ErrorSuccess)
                return null;

            // Trim trailing null terminator returned by Win32 length count
            var actualLen = length > 0 && buffer[length - 1] == '\0' ? (int)length - 1 : (int)length;
            return actualLen > 0 ? new string(buffer, 0, actualLen) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Defense-in-depth Store mode indicator: true if compiled with IsStoreBuild=true (STORE_MSIX)
    /// OR running inside an MSIX package container at runtime.
    /// </summary>
    public static bool IsStoreManagedRuntime() => IsCompileTimeStoreBuild || IsPackagedMsix();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}

public interface IDistributionProfile
{
    bool IsStoreManaged { get; }
    bool IsPackagedMsix { get; }
    string AppDisplayName { get; }
    string AppShortName { get; }
    string PrivacyPolicyUrl { get; }
    string GitHubRepositoryUrl { get; }
}

public sealed record DistributionProfile(
    bool IsStoreManaged,
    bool IsPackagedMsix,
    string AppDisplayName = "Tidejar - Video & Stream Saver",
    string AppShortName = "Tidejar",
    string PrivacyPolicyUrl = "https://github.com/braayounis14/YTDownloadTool/blob/main/PRIVACY.md",
    string GitHubRepositoryUrl = "https://github.com/braayounis14/YTDownloadTool") : IDistributionProfile
{
    public static DistributionProfile Current { get; } = new(
        IsStoreManaged: PackageIdentityHelper.IsStoreManagedRuntime(),
        IsPackagedMsix: PackageIdentityHelper.IsPackagedMsix());

    public static DistributionProfile ForTesting(bool isStoreManaged, bool isPackagedMsix = false) =>
        new(IsStoreManaged: isStoreManaged, IsPackagedMsix: isPackagedMsix);
}
