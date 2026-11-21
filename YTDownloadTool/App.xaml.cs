using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Diagnostics;
using YTDownloadTool.Core.Downloads;
using YTDownloadTool.Core.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Platform;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;
using YTDownloadTool.Core.Storage;
using YTDownloadTool.Core.Tools;
using YTDownloadTool.Services;
using YTDownloadTool.ViewModels;

namespace YTDownloadTool;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var paths = new DefaultAppPaths();
        paths.EnsureDirectoriesExist();

        var services = new ServiceCollection();
        ConfigureServices(services, paths);

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Tidejar starting up (StoreManaged={IsStore}, PackagedMsix={IsMsix}).",
            DistributionProfile.Current.IsStoreManaged, DistributionProfile.Current.IsPackagedMsix);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Tidejar shutting down.");

        if (_serviceProvider is { } provider)
        {
            _serviceProvider = null;
            try
            {
                // Dispose container asynchronously on a thread-pool thread with bounded timeout
                // to support IAsyncDisposable services cleanly without deadlocking the WPF Dispatcher.
                Task.Run(() => provider.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while disposing service provider during shutdown.");
            }
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services, DefaultAppPaths paths)
    {
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IDistributionProfile>(DistributionProfile.Current);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new RollingFileLoggerProvider(paths.LogsDirectory, LogLevel.Information));
        });

        services.AddSingleton(sp =>
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Tidejar/1.1 (+https://github.com/braayounis14/YTDownloadTool)");
            return client;
        });

        var currentExePath = Environment.ProcessPath;
        services.AddSingleton<IProcessRunner>(sp =>
            new SafeProcessRunner(sp.GetRequiredService<ILogger<SafeProcessRunner>>(), currentExePath));

        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ISqliteDatabaseManager, SqliteDatabaseManager>();
        services.AddSingleton<IDownloadJobRepository, SqliteDownloadJobRepository>();
        services.AddSingleton<IDiagnosticsRepository, SqliteDiagnosticsRepository>();
        services.AddSingleton<IHistoryStore, SqliteHistoryStore>();
        services.AddSingleton<IDiskSpaceReservationLedger>(_ => new DiskSpaceReservationLedger());
        services.AddSingleton<IMediaIntegrityService, MediaIntegrityService>();
        services.AddSingleton<IToolManager, ToolManager>();
        services.AddSingleton<OutputPathGuard>();
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IDownloadOrchestrator, DownloadOrchestrator>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();

        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI dispatcher exception.");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails have been written to the application log.",
            "Tidejar — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Unhandled AppDomain exception (IsTerminating={Term}).", e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
