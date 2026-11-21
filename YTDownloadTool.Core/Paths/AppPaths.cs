using YTDownloadTool.Core.Platform;

namespace YTDownloadTool.Core.Paths;

public interface IAppPaths
{
    string AppDirectory { get; }
    string BaselineToolsDirectory { get; }
    string DataRootDirectory { get; }
    string ToolsDirectory { get; }
    string ToolsManifestPath { get; }
    string ToolsStagingDirectory { get; }
    string TempDownloadsDirectory { get; }
    string LogsDirectory { get; }
    string SettingsRootDirectory { get; }
    string SettingsFilePath { get; }
    string HistoryFilePath { get; }
    string DatabaseFilePath { get; }
    string DatabaseBackupsDirectory { get; }
    string DefaultDownloadsDirectory { get; }
    string PrivacyPolicyFilePath { get; }
    string ThirdPartyNoticesFilePath { get; }

    void EnsureDirectoriesExist();
}

public sealed class DefaultAppPaths : IAppPaths
{
    public const string AppFolderName = "YTDownloadTool";
    public const string DataFolderName = "YTDownloadToolData";
    public const string StoreAppFolderName = "Tidejar";

    public DefaultAppPaths(
        string? appDirectory = null,
        string? localAppData = null,
        string? roamingAppData = null,
        string? videosFolder = null,
        string? msixPackageFamilyName = null)
    {
        AppDirectory = string.IsNullOrWhiteSpace(appDirectory) ? AppContext.BaseDirectory : appDirectory;
        var resolvedLocal = string.IsNullOrWhiteSpace(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData;
        var resolvedRoaming = string.IsNullOrWhiteSpace(roamingAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : roamingAppData;
        var resolvedVideos = string.IsNullOrWhiteSpace(videosFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            : videosFolder;

        // Only auto-detect MSIX package family name when caller did not supply isolated test directories.
        var isCustomSandbox = !string.IsNullOrWhiteSpace(localAppData) || !string.IsNullOrWhiteSpace(roamingAppData);
        var effectivePfn = !string.IsNullOrWhiteSpace(msixPackageFamilyName)
            ? msixPackageFamilyName
            : (isCustomSandbox ? null : PackageIdentityHelper.TryGetPackageFamilyName());

        BaselineToolsDirectory = Path.Combine(AppDirectory, "BaselineTools");
        PrivacyPolicyFilePath = Path.Combine(AppDirectory, "PRIVACY.md");
        ThirdPartyNoticesFilePath = Path.Combine(AppDirectory, "THIRD_PARTY_NOTICES.txt");

        if (!string.IsNullOrWhiteSpace(effectivePfn))
        {
            // Inside an MSIX container, write directly to the real physical Package LocalCache path
            // so SQLite WAL, backups, temp staging, and external Explorer ("Open Logs Folder")
            // all access the exact same real directory without VFS virtualization mismatch.
            var packageLocalCache = Path.Combine(resolvedLocal, "Packages", effectivePfn, "LocalCache");
            DataRootDirectory = Path.Combine(packageLocalCache, "Local", StoreAppFolderName);
            SettingsRootDirectory = Path.Combine(packageLocalCache, "Roaming", StoreAppFolderName);
            DefaultDownloadsDirectory = Path.Combine(resolvedVideos, StoreAppFolderName);
        }
        else
        {
            // Unpackaged (GitHub Velopack / portable): preserve existing folder layout for 100% upgrade compatibility.
            DataRootDirectory = Path.Combine(resolvedLocal, DataFolderName);
            SettingsRootDirectory = Path.Combine(resolvedRoaming, AppFolderName);
            DefaultDownloadsDirectory = Path.Combine(resolvedVideos, StoreAppFolderName);
        }

        ToolsDirectory = Path.Combine(DataRootDirectory, "Tools");
        ToolsManifestPath = Path.Combine(ToolsDirectory, "manifest.json");
        ToolsStagingDirectory = Path.Combine(ToolsDirectory, "staging");
        TempDownloadsDirectory = Path.Combine(DataRootDirectory, "Temp");
        LogsDirectory = Path.Combine(DataRootDirectory, "Logs");

        SettingsFilePath = Path.Combine(SettingsRootDirectory, "settings.json");
        HistoryFilePath = Path.Combine(SettingsRootDirectory, "history.json");
        DatabaseFilePath = Path.Combine(SettingsRootDirectory, "ytdownloadtool.db");
        DatabaseBackupsDirectory = Path.Combine(SettingsRootDirectory, "Backups");
    }

    public string AppDirectory { get; }
    public string BaselineToolsDirectory { get; }
    public string DataRootDirectory { get; }
    public string ToolsDirectory { get; }
    public string ToolsManifestPath { get; }
    public string ToolsStagingDirectory { get; }
    public string TempDownloadsDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsRootDirectory { get; }
    public string SettingsFilePath { get; }
    public string HistoryFilePath { get; }
    public string DatabaseFilePath { get; }
    public string DatabaseBackupsDirectory { get; }
    public string DefaultDownloadsDirectory { get; }
    public string PrivacyPolicyFilePath { get; }
    public string ThirdPartyNoticesFilePath { get; }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataRootDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(ToolsStagingDirectory);
        Directory.CreateDirectory(TempDownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SettingsRootDirectory);
        Directory.CreateDirectory(DatabaseBackupsDirectory);
    }
}
