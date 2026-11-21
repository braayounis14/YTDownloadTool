using System.IO;
#if !STORE_MSIX
using Velopack;
#endif
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Platform;
using YTDownloadTool.Core.Process;

namespace YTDownloadTool;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 1. Fast isolated CTRL_C signaler mode (exits before WPF initializes)
        if (ConsoleGracefulStopper.TryHandleInternalSignalArgs(args, out var signalExitCode))
        {
            return signalExitCode;
        }

#if !STORE_MSIX
        // 2. Velopack install / update / uninstall lifecycle hooks (GitHub desktop build only;
        //    also guarded at runtime against MSIX package execution as defense-in-depth for Policy 10.2.5)
        if (!PackageIdentityHelper.IsPackagedMsix())
        {
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => CleanupManagedDataOnUninstall())
                .Run();
        }
#endif

        // 3. Launch WPF Application
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

#if !STORE_MSIX
    private static void CleanupManagedDataOnUninstall()
    {
        // Clean managed external tools & logs in %LocalAppData%\YTDownloadToolData on uninstall.
        // NEVER touch user downloaded videos (%USERPROFILE%\Videos\...) or user roaming settings.
        try
        {
            var paths = new DefaultAppPaths();
            if (Directory.Exists(paths.DataRootDirectory))
            {
                Directory.Delete(paths.DataRootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup during uninstall; never block uninstaller.
        }
    }
#endif
}
