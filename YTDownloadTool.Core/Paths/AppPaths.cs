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

    void EnsureDirectoriesExist();
}

public sealed class DefaultAppPaths : IAppPaths
{
    public const string AppFolderName = "YTDownloadTool";
    public const string DataFolderName = "YTDownloadToolData";

    public DefaultAppPaths(string? appDirectory = null, string? localAppData = null, string? roamingAppData = null, string? videosFolder = null)
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

        BaselineToolsDirectory = Path.Combine(AppDirectory, "BaselineTools");
        DataRootDirectory = Path.Combine(resolvedLocal, DataFolderName);
        ToolsDirectory = Path.Combine(DataRootDirectory, "Tools");
        ToolsManifestPath = Path.Combine(ToolsDirectory, "manifest.json");
        ToolsStagingDirectory = Path.Combine(ToolsDirectory, "staging");
        TempDownloadsDirectory = Path.Combine(DataRootDirectory, "Temp");
        LogsDirectory = Path.Combine(DataRootDirectory, "Logs");

        SettingsRootDirectory = Path.Combine(resolvedRoaming, AppFolderName);
        SettingsFilePath = Path.Combine(SettingsRootDirectory, "settings.json");
        HistoryFilePath = Path.Combine(SettingsRootDirectory, "history.json");
        DatabaseFilePath = Path.Combine(SettingsRootDirectory, "ytdownloadtool.db");
        DatabaseBackupsDirectory = Path.Combine(SettingsRootDirectory, "Backups");

        DefaultDownloadsDirectory = Path.Combine(resolvedVideos, AppFolderName);
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
