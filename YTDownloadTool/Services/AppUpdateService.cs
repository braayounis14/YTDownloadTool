using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Services;

public sealed record AppUpdateState(
    bool IsUpdateAvailable,
    bool IsDownloadedReadyToRestart,
    string CurrentVersion,
    string? TargetVersion,
    string StatusMessage);

public interface IAppUpdateService
{
    string CurrentVersion { get; }
    AppUpdateState CurrentState { get; }
    event EventHandler<AppUpdateState>? StateChanged;

    Task<AppUpdateState> CheckAndDownloadIfAvailableAsync(bool isManualCheck, CancellationToken ct = default);
    void ApplyUpdateAndRestart();
}

public sealed class AppUpdateService : IAppUpdateService
{
    public const string GitHubRepositoryUrl = "https://github.com/braayounis14/YTDownloadTool";

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateInfo? _pendingUpdate;
    private AppUpdateState _currentState;

    public AppUpdateService(ISettingsStore settingsStore, ILogger<AppUpdateService> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        CurrentVersion = ResolveCurrentVersion();
        _currentState = new AppUpdateState(false, false, CurrentVersion, null, "Up to date");
    }

    public string CurrentVersion { get; }
    public AppUpdateState CurrentState => _currentState;
    public event EventHandler<AppUpdateState>? StateChanged;

    public async Task<AppUpdateState> CheckAndDownloadIfAvailableAsync(bool isManualCheck, CancellationToken ct = default)
    {
        await _checkLock.WaitAsync(ct);
        try
        {
            var settings = _settingsStore.Current;

            // Rate-limit guard for background startup checks (GitHub public API = 60 req/hr/IP).
            if (!isManualCheck)
            {
                if (!settings.AutoCheckAppUpdates)
                    return _currentState;

                if (settings.LastAppUpdateCheckUtc is { } lastCheck)
                {
                    var minInterval = TimeSpan.FromHours(Math.Clamp(settings.AppUpdateCheckIntervalHours, 12, 168));
                    if (DateTimeOffset.UtcNow - lastCheck < minInterval)
                    {
                        _logger.LogDebug("Skipping background app update check; last checked at {LastCheck}.", lastCheck);
                        return _currentState;
                    }
                }
            }

            var source = new GithubSource(GitHubRepositoryUrl, accessToken: null, prerelease: false);
            var mgr = new UpdateManager(source);

            if (!mgr.IsInstalled)
            {
                var devMsg = isManualCheck
                    ? "Running in portable / development mode (auto-update activates when installed via Setup.exe)."
                    : "Development mode";
                return PublishState(new AppUpdateState(false, false, CurrentVersion, null, devMsg));
            }

            // Record check timestamp before network request so repeated restarts do not hammer GitHub.
            settings.LastAppUpdateCheckUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(settings, ct);

            var updateInfo = await mgr.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return PublishState(new AppUpdateState(false, false, CurrentVersion, null, $"Up to date (v{CurrentVersion})"));
            }

            var targetVer = updateInfo.TargetFullRelease.Version.ToString();
            PublishState(new AppUpdateState(true, false, CurrentVersion, targetVer, $"Downloading update v{targetVer}..."));

            await mgr.DownloadUpdatesAsync(updateInfo, cancelToken: ct);
            _pendingUpdate = updateInfo;

            return PublishState(new AppUpdateState(
                IsUpdateAvailable: true,
                IsDownloadedReadyToRestart: true,
                CurrentVersion: CurrentVersion,
                TargetVersion: targetVer,
                StatusMessage: $"Update v{targetVer} is ready — restart to apply."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsRateLimitOrForbidden(ex))
            {
                _logger.LogDebug(ex, "GitHub release check rate-limited (HTTP 403/429).");
                if (!isManualCheck)
                {
                    // Silently ignore HTTP 403 rate limits during background checks.
                    return _currentState;
                }

                return PublishState(new AppUpdateState(false, false, CurrentVersion, null,
                    "Update server temporarily rate-limited. Please try again later."));
            }

            _logger.LogWarning(ex, "App update check failed (manual={Manual}).", isManualCheck);
            if (!isManualCheck)
                return _currentState;

            return PublishState(new AppUpdateState(false, false, CurrentVersion, null,
                $"Could not check for updates: {ex.Message}"));
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate is null)
            return;

        var source = new GithubSource(GitHubRepositoryUrl, accessToken: null, prerelease: false);
        var mgr = new UpdateManager(source);
        if (!mgr.IsInstalled)
            return;

        _logger.LogInformation("Applying downloaded Velopack update v{Version} and restarting.", _pendingUpdate.TargetFullRelease.Version);
        mgr.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }

    private AppUpdateState PublishState(AppUpdateState next)
    {
        _currentState = next;
        StateChanged?.Invoke(this, next);
        return next;
    }

    private static bool IsRateLimitOrForbidden(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException hre &&
                hre.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return true;
            }

            if (cur.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
                || cur.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
            return infoVer.Split('+')[0];

        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
