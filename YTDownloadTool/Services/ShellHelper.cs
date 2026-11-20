using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace YTDownloadTool.Services;

public static class ShellHelper
{
    public static void OpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true,
            Verb = "open"
        });
    }

    public static void RevealFileInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (File.Exists(filePath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            psi.ArgumentList.Add($"/select,{filePath}");
            Process.Start(psi);
            return;
        }

        var parent = Path.GetDirectoryName(filePath);
        OpenFolder(parent);
    }

    public static void OpenFileWithDefaultApp(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
    }

    public static string? BrowseForFolder(string? initialDirectory, string title = "Select Download Folder")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null or <= 0)
            return "--";

        double val = bytes.Value;
        if (val >= 1024.0 * 1024 * 1024) return $"{val / (1024.0 * 1024 * 1024):F2} GB";
        if (val >= 1024.0 * 1024) return $"{val / (1024.0 * 1024):F1} MB";
        if (val >= 1024.0) return $"{val / 1024.0:F0} KB";
        return $"{val:F0} B";
    }

    public static string FormatSpeed(double? bytesPerSecond)
    {
        if (bytesPerSecond is null or <= 0)
            return "--";

        var val = bytesPerSecond.Value;
        if (val >= 1024.0 * 1024) return $"{val / (1024.0 * 1024):F2} MB/s";
        if (val >= 1024.0) return $"{val / 1024.0:F0} KB/s";
        return $"{val:F0} B/s";
    }

    public static string FormatDuration(TimeSpan? duration)
    {
        if (!duration.HasValue || duration.Value < TimeSpan.Zero)
            return "--:--";

        var ts = duration.Value;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static string FormatSeconds(double? seconds) =>
        seconds is > 0 ? FormatDuration(TimeSpan.FromSeconds(seconds.Value)) : "--:--";
}
