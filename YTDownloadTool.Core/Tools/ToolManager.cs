using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YTDownloadTool.Core.Paths;
using YTDownloadTool.Core.Process;
using YTDownloadTool.Core.Settings;

namespace YTDownloadTool.Core.Tools;

public interface IToolManager
{
    Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default);
    Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default);
    Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default);
    Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default);
    Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default);
    Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class ToolManager : IToolManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ToolKind[] AllTools = [ToolKind.YtDlp, ToolKind.FFmpeg, ToolKind.Deno];

    private readonly IAppPaths _paths;
    private readonly ISettingsStore _settingsStore;
    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ToolManager> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private readonly Dictionary<ToolKind, ToolUpdateCheckResult> _lastChecks = new();

    public ToolManager(
        IAppPaths paths,
        ISettingsStore settingsStore,
        IProcessRunner processRunner,
        HttpClient httpClient,
        ILogger<ToolManager> logger)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _processRunner = processRunner;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ResolvedToolchain> EnsureToolsReadyAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);
            var resolvedPaths = new Dictionary<ToolKind, (string ExePath, string Version)>();

            foreach (var kind in AllTools)
            {
                var def = GetSourceDefinition(kind);
                status?.Report($"Checking {def.DisplayName}...");

                var active = await TryVerifyManagedVersionAsync(manifest, kind, usePrevious: false, ct);
                if (active is not null)
                {
                    resolvedPaths[kind] = active.Value;
                    continue;
                }

                // Active missing or corrupted: attempt automatic rollback to previous version
                status?.Report($"Recovering {def.DisplayName} from previous version...");
                var previous = await TryVerifyManagedVersionAsync(manifest, kind, usePrevious: true, ct);
                if (previous is not null && manifest.Tools.TryGetValue(kind, out var entry) && !string.IsNullOrWhiteSpace(entry.PreviousVersion))
                {
                    _logger.LogWarning("Active {Tool} version failed health check; rolling back to {Previous}.", kind, entry.PreviousVersion);
                    entry.ActiveVersion = entry.PreviousVersion;
                    entry.PreviousVersion = null;
                    await SaveManifestInternalAsync(manifest, ct);
                    resolvedPaths[kind] = previous.Value;
                    continue;
                }

                // Attempt seeding from read-only installer BaselineTools directory
                status?.Report($"Setting up {def.DisplayName}...");
                var seeded = await TrySeedFromBaselineInternalAsync(manifest, kind, ct);
                if (seeded is not null)
                {
                    await SaveManifestInternalAsync(manifest, ct);
                    resolvedPaths[kind] = seeded.Value;
                    continue;
                }

                // Final recovery: download verified official release automatically
                status?.Report($"Downloading {def.DisplayName}...");
                _logger.LogInformation("Baseline for {Tool} not found; downloading official verified release.", kind);
                var downloaded = await DownloadAndActivateInternalAsync(manifest, kind, progress: null, ct);
                resolvedPaths[kind] = downloaded;
            }

            var ffmpegExe = resolvedPaths[ToolKind.FFmpeg].ExePath;
            var ffmpegDir = Path.GetDirectoryName(ffmpegExe) ?? throw new InvalidOperationException("Invalid FFmpeg path.");
            var ffprobeExe = Path.Combine(ffmpegDir, "ffprobe.exe");

            status?.Report("Download engine ready.");

            return new ResolvedToolchain(
                YtDlpPath: resolvedPaths[ToolKind.YtDlp].ExePath,
                FFmpegDirectory: ffmpegDir,
                FFmpegPath: ffmpegExe,
                FFprobePath: ffprobeExe,
                DenoPath: resolvedPaths[ToolKind.Deno].ExePath,
                YtDlpVersion: resolvedPaths[ToolKind.YtDlp].Version,
                FFmpegVersion: resolvedPaths[ToolKind.FFmpeg].Version,
                DenoVersion: resolvedPaths[ToolKind.Deno].Version);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<IReadOnlyList<ToolStatusInfo>> GetStatusesAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);
            var statuses = new List<ToolStatusInfo>(AllTools.Length);

            foreach (var kind in AllTools)
            {
                statuses.Add(await BuildStatusInfoInternalAsync(manifest, kind, ct));
            }

            return statuses;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<ToolUpdateCheckResult> CheckForUpdateAsync(ToolKind kind, bool force = false, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);
            return await CheckForUpdateInternalAsync(manifest, kind, force, ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<IReadOnlyList<ToolUpdateCheckResult>> CheckAllForUpdatesAsync(bool force = false, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);
            var results = new List<ToolUpdateCheckResult>(AllTools.Length);

            foreach (var kind in AllTools)
            {
                try
                {
                    results.Add(await CheckForUpdateInternalAsync(manifest, kind, force, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Update check failed for {Tool}.", kind);
                    var def = GetSourceDefinition(kind);
                    results.Add(new ToolUpdateCheckResult(kind, false, null, null, null, null, null, def.AssetName, $"Check failed: {ex.Message}"));
                }
            }

            return results;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<ToolStatusInfo> UpdateToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);
            await DownloadAndActivateInternalAsync(manifest, kind, progress, ct);
            _lastChecks.Remove(kind);
            return await BuildStatusInfoInternalAsync(manifest, kind, ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<ToolStatusInfo> RollbackToolAsync(ToolKind kind, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);

            if (!manifest.Tools.TryGetValue(kind, out var entry) || string.IsNullOrWhiteSpace(entry.PreviousVersion))
                throw new InvalidOperationException($"No previous version is available to roll back {kind}.");

            var previousCheck = await TryVerifyManagedVersionAsync(manifest, kind, usePrevious: true, ct);
            if (previousCheck is null)
                throw new InvalidOperationException($"Previous version '{entry.PreviousVersion}' of {kind} is missing or failed verification.");

            var oldActive = entry.ActiveVersion;
            entry.ActiveVersion = entry.PreviousVersion;
            entry.PreviousVersion = oldActive;
            await SaveManifestInternalAsync(manifest, ct);

            _logger.LogInformation("Rolled back {Tool} from {Old} to {New}.", kind, oldActive, entry.ActiveVersion);
            return await BuildStatusInfoInternalAsync(manifest, kind, ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<ToolStatusInfo> RepairToolAsync(ToolKind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _paths.EnsureDirectoriesExist();
            var manifest = await LoadManifestInternalAsync(ct);

            var seeded = await TrySeedFromBaselineInternalAsync(manifest, kind, ct);
            if (seeded is not null)
            {
                await SaveManifestInternalAsync(manifest, ct);
                _logger.LogInformation("Repaired {Tool} from bundled baseline ({Version}).", kind, seeded.Value.Version);
                return await BuildStatusInfoInternalAsync(manifest, kind, ct);
            }

            await DownloadAndActivateInternalAsync(manifest, kind, progress, ct);
            return await BuildStatusInfoInternalAsync(manifest, kind, ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private ToolSourceDefinition GetSourceDefinition(ToolKind kind) =>
        ToolSources.GetDefinition(kind, _settingsStore.Current.YtDlpChannel);

    private async Task<ToolUpdateCheckResult> CheckForUpdateInternalAsync(ToolManifest manifest, ToolKind kind, bool force, CancellationToken ct)
    {
        var def = GetSourceDefinition(kind);
        manifest.Tools.TryGetValue(kind, out var entry);

        if (!force && entry?.LastCheckedUtc is { } lastChecked)
        {
            var elapsed = DateTimeOffset.UtcNow - lastChecked;
            if (elapsed < def.AutoCheckCadence && _lastChecks.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            if (elapsed < def.AutoCheckCadence)
            {
                var skipped = new ToolUpdateCheckResult(kind, false, entry.ActiveVersion, entry.ActiveVersion, entry.Sha256, null, null, def.AssetName, "Checked recently.");
                _lastChecks[kind] = skipped;
                return skipped;
            }
        }

        string tag;
        string checksumUrl;
        string downloadUrl;

        if (def.UsesRollingLatestTag)
        {
            tag = "latest";
            checksumUrl = ToolSources.BuildLatestAssetDownloadUrl(def, def.ChecksumAssetName);
            downloadUrl = ToolSources.BuildLatestAssetDownloadUrl(def, def.AssetName);
        }
        else
        {
            tag = await ResolveLatestReleaseTagAsync(def, ct);
            checksumUrl = ToolSources.BuildTagAssetDownloadUrl(def, tag, def.ChecksumAssetName);
            downloadUrl = ToolSources.BuildTagAssetDownloadUrl(def, tag, def.AssetName);
        }

        var checksumContent = await FetchTrustedTextAsync(checksumUrl, ct);
        var expectedSha = ChecksumVerifier.ParseExpectedSha256(checksumContent, def.AssetName);
        if (string.IsNullOrWhiteSpace(expectedSha))
        {
            throw new InvalidDataException($"Could not locate SHA-256 checksum for '{def.AssetName}' in '{def.ChecksumAssetName}'.");
        }

        bool updateAvailable;
        string latestVersionLabel;

        if (def.UsesRollingLatestTag)
        {
            // Rolling releases (yt-dlp/FFmpeg-Builds) distinguish builds by official asset SHA-256.
            latestVersionLabel = $"latest-{expectedSha[..8]}";
            updateAvailable = entry is null
                || !string.Equals(entry.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            latestVersionLabel = tag.TrimStart('v', 'V');
            var currentNorm = entry?.ActiveVersion?.TrimStart('v', 'V');
            updateAvailable = entry is null
                || !string.Equals(currentNorm, latestVersionLabel, StringComparison.OrdinalIgnoreCase)
                || (kind == ToolKind.YtDlp && !string.Equals(entry.Channel, _settingsStore.Current.YtDlpChannel, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is not null)
        {
            entry.LastCheckedUtc = DateTimeOffset.UtcNow;
            await SaveManifestInternalAsync(manifest, ct);
        }

        var result = new ToolUpdateCheckResult(
            Kind: kind,
            UpdateAvailable: updateAvailable,
            CurrentVersion: entry?.ActiveVersion,
            LatestVersion: latestVersionLabel,
            LatestAssetSha256: expectedSha,
            DownloadUrl: downloadUrl,
            ChecksumUrl: checksumUrl,
            AssetName: def.AssetName,
            Message: updateAvailable
                ? $"Update available: {latestVersionLabel}"
                : "Up to date.");

        _lastChecks[kind] = result;
        return result;
    }

    private async Task<(string ExePath, string Version)> DownloadAndActivateInternalAsync(
        ToolManifest manifest,
        ToolKind kind,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var def = GetSourceDefinition(kind);
        var check = await CheckForUpdateInternalAsync(manifest, kind, force: true, ct);

        if (string.IsNullOrWhiteSpace(check.DownloadUrl) || string.IsNullOrWhiteSpace(check.LatestAssetSha256))
            throw new InvalidOperationException($"Unable to resolve download URL or SHA-256 checksum for {def.DisplayName}.");

        var stagingRunDir = Path.Combine(_paths.ToolsStagingDirectory, GetToolFolderName(kind), Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(stagingRunDir, "extracted");
        Directory.CreateDirectory(stagingRunDir);

        try
        {
            var downloadFilePath = Path.Combine(stagingRunDir, def.AssetName);
            _logger.LogInformation("Downloading {Tool} from {Url}", def.DisplayName, check.DownloadUrl);

            await DownloadTrustedFileAsync(check.DownloadUrl, downloadFilePath, progress, ct);

            // Mandatory cryptographic SHA-256 verification before extraction or execution
            var shaValid = await ChecksumVerifier.VerifyFileSha256Async(downloadFilePath, check.LatestAssetSha256, ct);
            if (!shaValid)
            {
                throw new InvalidDataException(
                    $"Integrity check failed for {def.DisplayName}: downloaded file SHA-256 did not match official {def.ChecksumAssetName}.");
            }

            Directory.CreateDirectory(extractDir);
            if (def.IsZipArchive)
            {
                Func<System.IO.Compression.ZipArchiveEntry, string?> mapper = kind switch
                {
                    ToolKind.FFmpeg => SafeZipExtractor.MapFFmpegSharedEntry,
                    ToolKind.Deno => SafeZipExtractor.MapDenoEntry,
                    _ => throw new NotSupportedException($"No archive extractor defined for {kind}.")
                };

                SafeZipExtractor.ExtractFiltered(downloadFilePath, extractDir, mapper);
            }
            else
            {
                File.Move(downloadFilePath, Path.Combine(extractDir, def.AssetName), overwrite: true);
            }

            // Pre-activation health probe on staged binaries before touching manifest or active folder
            var probed = await ProbeToolDirectoryAsync(kind, extractDir, ct);
            if (probed is null)
            {
                throw new InvalidOperationException(
                    $"Staged {def.DisplayName} binaries failed pre-activation health verification.");
            }

            var versionFolderName = def.UsesRollingLatestTag
                ? ToolVersionParser.SanitizeVersionForFolder($"{probed.Value.Version}_{check.LatestAssetSha256[..8]}")
                : ToolVersionParser.SanitizeVersionForFolder(probed.Value.Version);

            var finalVersionDir = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), versionFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(finalVersionDir)!);

            // If exact same version folder already exists (e.g., repair), copy into a clean unique suffix if locked
            if (Directory.Exists(finalVersionDir))
            {
                try
                {
                    Directory.Delete(finalVersionDir, recursive: true);
                }
                catch (IOException)
                {
                    versionFolderName = $"{versionFolderName}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    finalVersionDir = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), versionFolderName);
                }
            }

            MoveOrCopyDirectory(extractDir, finalVersionDir);

            // Re-verify final executable path
            var activatedExe = GetMainExecutablePath(kind, finalVersionDir);
            if (!File.Exists(activatedExe))
                throw new FileNotFoundException($"Expected executable missing after activation: {activatedExe}", activatedExe);

            // Atomically update manifest while preserving PreviousVersion for instant rollback
            manifest.Tools.TryGetValue(kind, out var existing);
            var previousVersion = existing is not null && !string.Equals(existing.ActiveVersion, versionFolderName, StringComparison.OrdinalIgnoreCase)
                ? existing.ActiveVersion
                : existing?.PreviousVersion;

            manifest.Tools[kind] = new ToolManifestEntry
            {
                ActiveVersion = versionFolderName,
                PreviousVersion = previousVersion,
                Channel = kind == ToolKind.YtDlp ? _settingsStore.Current.YtDlpChannel : "stable",
                Sha256 = check.LatestAssetSha256,
                SourceUrl = check.DownloadUrl,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                LastCheckedUtc = DateTimeOffset.UtcNow
            };

            await SaveManifestInternalAsync(manifest, ct);
            PruneOldToolVersions(kind, versionFolderName, previousVersion);

            _logger.LogInformation("Activated {Tool} version {Version} (previous={Previous}).", kind, versionFolderName, previousVersion ?? "none");
            return (activatedExe, probed.Value.Version);
        }
        finally
        {
            TryDeleteDirectory(stagingRunDir);
        }
    }

    private async Task<(string ExePath, string Version)?> TrySeedFromBaselineInternalAsync(ToolManifest manifest, ToolKind kind, CancellationToken ct)
    {
        var baselineDir = Path.Combine(_paths.BaselineToolsDirectory, GetToolFolderName(kind));
        if (!Directory.Exists(baselineDir))
            return null;

        var probed = await ProbeToolDirectoryAsync(kind, baselineDir, ct);
        if (probed is null)
        {
            _logger.LogWarning("Baseline directory for {Tool} exists at {Path} but failed verification.", kind, baselineDir);
            return null;
        }

        var versionFolder = ToolVersionParser.SanitizeVersionForFolder(probed.Value.Version);
        var destDir = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), versionFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(destDir)!);

        if (Directory.Exists(destDir))
        {
            try
            {
                Directory.Delete(destDir, recursive: true);
            }
            catch (IOException)
            {
                versionFolder = $"{versionFolder}_seed_{DateTime.UtcNow:yyyyMMddHHmmss}";
                destDir = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), versionFolder);
            }
        }

        CopyDirectory(baselineDir, destDir);

        var mainExe = GetMainExecutablePath(kind, destDir);
        var baselineArchiveSha = TryReadBaselineArchiveSha256(kind);
        var recordedSha = !string.IsNullOrWhiteSpace(baselineArchiveSha)
            ? baselineArchiveSha
            : (File.Exists(mainExe) ? await ChecksumVerifier.ComputeFileSha256Async(mainExe, ct) : string.Empty);

        manifest.Tools[kind] = new ToolManifestEntry
        {
            ActiveVersion = versionFolder,
            PreviousVersion = manifest.Tools.TryGetValue(kind, out var existing) && existing.ActiveVersion != versionFolder
                ? existing.ActiveVersion
                : null,
            Channel = kind == ToolKind.YtDlp ? _settingsStore.Current.YtDlpChannel : "stable",
            Sha256 = recordedSha,
            SourceUrl = "bundled-baseline",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            LastCheckedUtc = null
        };

        _logger.LogInformation("Seeded {Tool} ({Version}) from bundled baseline.", kind, probed.Value.Version);
        return (mainExe, probed.Value.Version);
    }

    private string? TryReadBaselineArchiveSha256(ToolKind kind)
    {
        var checksumsPath = Path.Combine(_paths.BaselineToolsDirectory, "baseline-checksums.json");
        if (!File.Exists(checksumsPath))
            return null;

        try
        {
            var json = File.ReadAllText(checksumsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            string[] candidateKeys = kind switch
            {
                ToolKind.YtDlp => ["ytDlp", "yt-dlp", "YtDlp"],
                ToolKind.FFmpeg => ["ffmpeg", "FFmpeg"],
                ToolKind.Deno => ["deno", "Deno"],
                _ => []
            };

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!candidateKeys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var value = prop.Value.GetString()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(value)
                    && value.Length == 64
                    && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse baseline-checksums.json; falling back to executable hash.");
        }

        return null;
    }

    private async Task<(string ExePath, string Version)?> TryVerifyManagedVersionAsync(
        ToolManifest manifest,
        ToolKind kind,
        bool usePrevious,
        CancellationToken ct)
    {
        if (!manifest.Tools.TryGetValue(kind, out var entry))
            return null;

        var versionFolder = usePrevious ? entry.PreviousVersion : entry.ActiveVersion;
        if (string.IsNullOrWhiteSpace(versionFolder))
            return null;

        var dir = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), versionFolder);
        if (!Directory.Exists(dir))
            return null;

        return await ProbeToolDirectoryAsync(kind, dir, ct);
    }

    private async Task<(string ExePath, string Version)?> ProbeToolDirectoryAsync(ToolKind kind, string directory, CancellationToken ct)
    {
        var mainExe = GetMainExecutablePath(kind, directory);
        if (!File.Exists(mainExe))
            return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            switch (kind)
            {
                case ToolKind.YtDlp:
                {
                    var res = await _processRunner.RunAsync(new ProcessRunRequest
                    {
                        ExecutablePath = mainExe,
                        Arguments = ["--version"],
                        GracefulStop = GracefulStopStrategy.KillImmediate
                    }, timeoutCts.Token);

                    if (res.ExitCode != 0)
                        return null;

                    var version = ToolVersionParser.ParseYtDlpVersion(res.StandardOutput);
                    return string.IsNullOrWhiteSpace(version) ? null : (mainExe, version);
                }

                case ToolKind.FFmpeg:
                {
                    var ffprobeExe = Path.Combine(directory, "ffprobe.exe");
                    if (!File.Exists(ffprobeExe))
                        return null;

                    var ffmpegRes = await _processRunner.RunAsync(new ProcessRunRequest
                    {
                        ExecutablePath = mainExe,
                        Arguments = ["-version"],
                        GracefulStop = GracefulStopStrategy.KillImmediate
                    }, timeoutCts.Token);

                    if (ffmpegRes.ExitCode != 0)
                        return null;

                    var ffprobeRes = await _processRunner.RunAsync(new ProcessRunRequest
                    {
                        ExecutablePath = ffprobeExe,
                        Arguments = ["-version"],
                        GracefulStop = GracefulStopStrategy.KillImmediate
                    }, timeoutCts.Token);

                    if (ffprobeRes.ExitCode != 0)
                        return null;

                    var version = ToolVersionParser.ParseFFmpegVersion(ffmpegRes.StandardOutput);
                    return string.IsNullOrWhiteSpace(version) ? null : (mainExe, version);
                }

                case ToolKind.Deno:
                {
                    var res = await _processRunner.RunAsync(new ProcessRunRequest
                    {
                        ExecutablePath = mainExe,
                        Arguments = ["--version"],
                        GracefulStop = GracefulStopStrategy.KillImmediate
                    }, timeoutCts.Token);

                    if (res.ExitCode != 0)
                        return null;

                    var version = ToolVersionParser.ParseDenoVersion(res.StandardOutput);
                    if (!ToolVersionParser.IsDenoVersionSupported(version))
                    {
                        _logger.LogWarning("Deno version '{Version}' is below minimum supported {Min}.", version, ToolVersionParser.MinimumDenoVersion);
                        return null;
                    }

                    return (mainExe, version!);
                }

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Health probe failed for {Tool} in {Dir}.", kind, directory);
            return null;
        }
    }

    private async Task<ToolStatusInfo> BuildStatusInfoInternalAsync(ToolManifest manifest, ToolKind kind, CancellationToken ct)
    {
        var def = GetSourceDefinition(kind);
        manifest.Tools.TryGetValue(kind, out var entry);
        _lastChecks.TryGetValue(kind, out var lastCheck);

        var verified = await TryVerifyManagedVersionAsync(manifest, kind, usePrevious: false, ct);
        var hasPrevious = !string.IsNullOrWhiteSpace(entry?.PreviousVersion)
                          && Directory.Exists(Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind), entry.PreviousVersion));

        if (verified is null)
        {
            var state = entry is null ? ToolHealthState.Missing : ToolHealthState.Corrupted;
            return new ToolStatusInfo(
                Kind: kind,
                DisplayName: def.DisplayName,
                State: state,
                InstalledVersion: entry?.ActiveVersion,
                PreviousVersion: hasPrevious ? entry?.PreviousVersion : null,
                AvailableVersion: lastCheck?.LatestVersion,
                UpdateAvailable: true,
                CanRollback: hasPrevious,
                ExecutablePath: null,
                LastCheckedUtc: entry?.LastCheckedUtc,
                Message: state == ToolHealthState.Missing ? "Not installed yet" : "Verification failed — click Repair");
        }

        var updateAvailable = lastCheck?.UpdateAvailable == true;
        return new ToolStatusInfo(
            Kind: kind,
            DisplayName: def.DisplayName,
            State: updateAvailable ? ToolHealthState.Outdated : ToolHealthState.Ready,
            InstalledVersion: verified.Value.Version,
            PreviousVersion: hasPrevious ? entry?.PreviousVersion : null,
            AvailableVersion: lastCheck?.LatestVersion,
            UpdateAvailable: updateAvailable,
            CanRollback: hasPrevious,
            ExecutablePath: verified.Value.ExePath,
            LastCheckedUtc: entry?.LastCheckedUtc,
            Message: updateAvailable ? $"Update {lastCheck!.LatestVersion} available" : "Ready");
    }

    private async Task<string> ResolveLatestReleaseTagAsync(ToolSourceDefinition def, CancellationToken ct)
    {
        var latestUrl = ToolSources.BuildLatestReleasePageUrl(def);
        ValidateTrustedUrl(latestUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, latestUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // If the handler didn't auto-redirect (e.g., 302 Location header), read Location directly
        var finalUri = response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            ? response.Headers.Location
            : response.RequestMessage?.RequestUri;

        if (finalUri is not null && !finalUri.IsAbsoluteUri)
            finalUri = new Uri(new Uri(latestUrl), finalUri);

        if (finalUri is null || !ToolSources.IsTrustedDownloadUri(finalUri))
            throw new InvalidOperationException($"Untrusted redirect URI while resolving latest release for {def.DisplayName}.");

        const string tagMarker = "/releases/tag/";
        var absolute = finalUri.AbsoluteUri;
        var idx = absolute.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tag = Uri.UnescapeDataString(absolute[(idx + tagMarker.Length)..].TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(tag))
                return tag;
        }

        // Fallback to GitHub Releases REST API if redirect did not point to /releases/tag/
        var apiUrl = $"https://api.github.com/repos/{def.Owner}/{def.Repository}/releases/latest";
        using var apiReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        apiReq.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var apiResp = await _httpClient.SendAsync(apiReq, ct);
        apiResp.EnsureSuccessStatusCode();

        await using var stream = await apiResp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var apiTag = doc.RootElement.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(apiTag))
            throw new InvalidDataException($"Could not determine latest release tag for {def.DisplayName}.");

        return apiTag;
    }

    private async Task<string> FetchTrustedTextAsync(string url, CancellationToken ct)
    {
        ValidateTrustedUrl(url);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        ValidateTrustedUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task DownloadTrustedFileAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateTrustedUrl(url);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        ValidateTrustedUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url);

        var totalBytes = response.Content.Headers.ContentLength;
        await using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp((double)totalRead / totalBytes.Value * 100.0, 0, 100));
            }
        }

        await fileStream.FlushAsync(ct);
        progress?.Report(100);
    }

    private static void ValidateTrustedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ToolSources.IsTrustedDownloadUri(uri))
        {
            throw new InvalidOperationException($"Blocked untrusted download URL: '{url}'.");
        }
    }

    private async Task<ToolManifest> LoadManifestInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_paths.ToolsManifestPath))
            return new ToolManifest();

        try
        {
            await using var stream = File.OpenRead(_paths.ToolsManifestPath);
            return await JsonSerializer.DeserializeAsync<ToolManifest>(stream, JsonOptions, ct) ?? new ToolManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Tool manifest unreadable; rebuilding manifest.");
            return new ToolManifest();
        }
    }

    private async Task SaveManifestInternalAsync(ToolManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ToolsDirectory);
        var tempPath = $"{_paths.ToolsManifestPath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempPath, _paths.ToolsManifestPath, overwrite: true);
    }

    private void PruneOldToolVersions(ToolKind kind, string keepActive, string? keepPrevious)
    {
        var toolRoot = Path.Combine(_paths.ToolsDirectory, GetToolFolderName(kind));
        if (!Directory.Exists(toolRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(toolRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, keepActive, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(keepPrevious) && string.Equals(name, keepPrevious, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryDeleteDirectory(dir);
        }
    }

    private static string GetToolFolderName(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => "yt-dlp",
        ToolKind.FFmpeg => "ffmpeg",
        ToolKind.Deno => "deno",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string GetMainExecutablePath(ToolKind kind, string directory) => kind switch
    {
        ToolKind.YtDlp => Path.Combine(directory, "yt-dlp.exe"),
        ToolKind.FFmpeg => Path.Combine(directory, "ffmpeg.exe"),
        ToolKind.Deno => Path.Combine(directory, "deno.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void MoveOrCopyDirectory(string sourceDir, string destDir)
    {
        try
        {
            Directory.Move(sourceDir, destDir);
        }
        catch (IOException)
        {
            CopyDirectory(sourceDir, destDir);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Folder may still be locked by an in-flight download; will be cleaned on next cycle.
        }
    }
}
