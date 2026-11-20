using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Paths;

namespace YTDownloadTool.Core.Settings;

public interface ISettingsStore
{
    AppSettings Current { get; }
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsStore> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private AppSettings _current;

    public SettingsStore(IAppPaths paths, ILogger<SettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
        _current = new AppSettings();
        _current.Normalize(_paths.DefaultDownloadsDirectory);
    }

    public AppSettings Current => _current.Clone();

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();

            if (!File.Exists(_paths.SettingsFilePath))
            {
                var defaults = new AppSettings();
                defaults.Normalize(_paths.DefaultDownloadsDirectory);
                _current = defaults;
                await WriteAtomicAsync(defaults, ct);
                return _current.Clone();
            }

            try
            {
                await using var stream = File.OpenRead(_paths.SettingsFilePath);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct) ?? new AppSettings();
                loaded.Normalize(_paths.DefaultDownloadsDirectory);
                _current = loaded;
                return _current.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Settings file was corrupted; backing up and restoring defaults.");
                TryBackupCorrupted(_paths.SettingsFilePath);

                var defaults = new AppSettings();
                defaults.Normalize(_paths.DefaultDownloadsDirectory);
                _current = defaults;
                await WriteAtomicAsync(defaults, ct);
                return _current.Clone();
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _ioLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var copy = settings.Clone();
            copy.Normalize(_paths.DefaultDownloadsDirectory);
            await WriteAtomicAsync(copy, ct);
            _current = copy;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task WriteAtomicAsync(AppSettings settings, CancellationToken ct)
    {
        var tempPath = $"{_paths.SettingsFilePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempPath, _paths.SettingsFilePath, overwrite: true);
    }

    private static void TryBackupCorrupted(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.bak", overwrite: true);
            }
        }
        catch
        {
            // Best-effort backup.
        }
    }
}
