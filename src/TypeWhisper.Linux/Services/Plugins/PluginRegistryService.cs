using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Fetches the Linux plugin registry, exposes compatible releases, and installs
///     validated archives through a recoverable directory transaction.
/// </summary>
public sealed class PluginRegistryService
{
    private const string RegistryUrl =
        "https://csmashe.github.io/typewhisper-linux/plugins.json";
    private const string HostPlatform = "linux";
    private const string HostSdkAbi = "net10.0";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;

    private const string TransactionDirectoryName = ".typewhisper-plugin-transactions";
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromMinutes(5);

    // First-run auto-install runs inside the awaited bootstrap, so a blackholed registry
    // endpoint must not hold onboarding for HttpClient's ~100s default timeout.
    private static readonly TimeSpan s_registryFetchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_updateCheckInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ManagedDirectoryTransaction _directoryTransaction;
    private readonly HttpClient _httpClient;
    private readonly PluginManager _pluginManager;
    private readonly string _pluginsRoot;
    private readonly ISettingsService _settings;

    private List<RegistryPlugin>? _cachedRegistry;
    private DateTime _cacheTimestamp;
    private DateTime _lastUpdateCheck;

    public PluginRegistryService(
        PluginManager pluginManager,
        PluginLoader pluginLoader,
        ISettingsService settings,
        HttpClient? httpClient = null
    )
        : this(
            pluginManager,
            pluginLoader,
            settings,
            httpClient ?? new HttpClient(),
            TypeWhisperEnvironment.PluginsPath
        ) { }

    internal PluginRegistryService(
        PluginManager pluginManager,
        PluginLoader pluginLoader,
        ISettingsService settings,
        HttpClient httpClient,
        string pluginsRoot
    )
    {
        ArgumentNullException.ThrowIfNull(pluginManager);
        ArgumentNullException.ThrowIfNull(pluginLoader);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);

        _pluginManager = pluginManager;
        _settings = settings;
        _httpClient = httpClient;
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _directoryTransaction = new ManagedDirectoryTransaction(
            Path.Join(_pluginsRoot, TransactionDirectoryName),
            // ReSharper disable once RedundantArgumentDefaultValue -- an interrupted install must
            // roll back to the previously working plugin; state that here rather than leaving it
            // to whatever the transaction's default happens to be.
            ManagedDirectoryRecoveryMode.RestoreBackup
        );
    }

    // Internal deterministic seams for compatibility tests.
    internal string HostVersion { get; init; } = AppVersion.Display;
    internal string RuntimeRid { get; init; } = ResolveRuntimeRid();

    public async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryAsync(
        CancellationToken ct = default
    )
    {
        var (plugins, _) = await FetchRegistryWithOutcomeAsync(ct).ConfigureAwait(false);
        return plugins;
    }

    /// <summary>
    ///     Fetches the registry and reports whether <em>this</em> call succeeded. Returned rather
    ///     than stored: first-run auto-install and the periodic update check can overlap, and a
    ///     shared field would let one caller read the other's result.
    /// </summary>
    private async Task<(IReadOnlyList<RegistryPlugin> Plugins, bool Succeeded)>
        FetchRegistryWithOutcomeAsync(CancellationToken ct)
    {
        if (_cachedRegistry is not null && DateTime.UtcNow - _cacheTimestamp < s_cacheDuration)
        {
            ct.ThrowIfCancellationRequested();
            return (_cachedRegistry, true);
        }

        try
        {
            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fetchCts.CancelAfter(s_registryFetchTimeout);
            var json = await _httpClient
                .GetStringAsync(RegistryUrl, fetchCts.Token)
                .ConfigureAwait(false);
            var allPlugins =
                JsonSerializer.Deserialize<List<RegistryPlugin>>(json, s_jsonOptions) ?? [];

            _cachedRegistry = allPlugins.Where(IsCompatible).ToList();
            _cacheTimestamp = DateTime.UtcNow;

            Trace.WriteLine(
                $"[PluginRegistry] Fetched {_cachedRegistry.Count} compatible Linux plugin(s) from registry"
            );
            return (_cachedRegistry, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only the caller's own token aborts. The 15s fetch deadline surfaces as an
            // OperationCanceledException too, and that one is an ordinary fetch failure.
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to fetch registry: {ex.Message}");
            return (_cachedRegistry ?? [], false);
        }
    }

    public PluginInstallState GetInstallState(RegistryPlugin registryPlugin)
    {
        var local = _pluginManager.GetPlugin(registryPlugin.Id);
        if (local is null)
        {
            return PluginInstallState.NotInstalled;
        }

        return AppVersion.TryCompareStrict(
                registryPlugin.Version,
                local.Manifest.Version,
                out var comparison
            )
            && comparison > 0
            ? PluginInstallState.UpdateAvailable
            : PluginInstallState.Installed;
    }

    /// <summary>
    ///     Restores any pre-activation swap left behind by a terminated process. This must run
    ///     before PluginManager discovers live plugin directories during startup.
    /// </summary>
    public async Task RecoverInterruptedInstallsAsync(CancellationToken ct = default)
    {
        await _directoryTransaction.RecoverAllAsync(ct).ConfigureAwait(false);
        await _directoryTransaction.PurgeAbandonedArtifactsAsync(ct).ConfigureAwait(false);
    }

    // Only reached for ids the host does not already bundle: BundledPluginDeployer runs
    // first every launch and re-syncs any bundled tree it does not recognise, so wiring
    // this to an update button needs a bundled-vs-registry ownership rule first.
    internal async Task InstallPluginAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default
    )
    {
        EnsureRegistryEntryInstallable(registryPlugin);
        Directory.CreateDirectory(_pluginsRoot);

        var pluginDir = Path.Join(_pluginsRoot, registryPlugin.Id);
        var stage = _directoryTransaction.CreateStageDirectory(registryPlugin.Id);
        var downloadPath = Path.Join(
            Path.GetDirectoryName(stage)!,
            $"download-{Guid.NewGuid():N}.tmp"
        );
        ManagedDirectoryTransaction.ManagedDirectoryCommit? commit = null;
        var oldWasLoaded = _pluginManager.GetPlugin(registryPlugin.Id) is not null;
        var activationSucceeded = false;
        try
        {
            // Everything below the download runs on the caller's context: unload/activate
            // persist enabled state, and SettingsChanged reaches settings view models that
            // mutate bound collections without dispatching. Task.Run keeps the download and
            // extraction off that context, since the caller is usually the UI thread.
            await Task.Run(
                async () =>
                {
                    await DownloadArchiveAsync(registryPlugin, downloadPath, progress, ct)
                        .ConfigureAwait(false);
                    ExtractAndValidateArchive(downloadPath, stage, registryPlugin, ct);
                },
                ct
            );

            commit = await _directoryTransaction.CommitAsync(
                registryPlugin.Id,
                stage,
                pluginDir,
                async cancellationToken =>
                {
                    if (_pluginManager.GetPlugin(registryPlugin.Id) is not null)
                    {
                        await _pluginManager.UnloadPluginAsync(registryPlugin.Id);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                },
                ct
            );

            if (!await _pluginManager.LoadPluginFromDirectoryAsync(pluginDir, activate: true))
            {
                throw new InvalidDataException(
                    $"Plugin '{registryPlugin.Id}' could not be loaded and activated."
                );
            }

            activationSucceeded = true;
            await commit.CompleteAsync();
            Trace.WriteLine(
                $"[PluginRegistry] Installed plugin: {registryPlugin.Id} v{registryPlugin.Version}"
            );
        }
        catch (Exception installException)
        {
            Trace.WriteLine(
                $"[PluginRegistry] Failed to install {registryPlugin.Id}: {installException.Message}"
            );

            var recoveryFailures = new List<Exception>();
            if (commit is not null && !activationSucceeded)
            {
                try
                {
                    var rejected = await commit.RollbackAsync();
                    if (rejected is not null)
                    {
                        // Discard immediately: FirstRunAutoInstallAsync retries a failing
                        // plugin on every launch, so a retained tree per attempt is unbounded.
                        DeleteDirectoryBestEffort(rejected);
                    }
                }
                catch (Exception rollbackException)
                {
                    recoveryFailures.Add(rollbackException);
                }
            }

            if (
                oldWasLoaded
                && _pluginManager.GetPlugin(registryPlugin.Id) is null
                && Directory.Exists(pluginDir)
            )
            {
                try
                {
                    if (
                        !await _pluginManager.LoadPluginFromDirectoryAsync(
                            pluginDir,
                            activate: true
                        )
                    )
                    {
                        throw new InvalidDataException(
                            $"The previous plugin '{registryPlugin.Id}' could not be reloaded after rollback."
                        );
                    }
                }
                catch (Exception reloadException)
                {
                    recoveryFailures.Add(reloadException);
                }
            }

            if (recoveryFailures.Count > 0)
            {
                throw new AggregateException(
                    "Plugin installation failed and recovery was not fully successful.",
                    [installException, .. recoveryFailures]
                );
            }

            throw;
        }
        finally
        {
            if (commit is not null)
            {
                await commit.DisposeAsync();
            }

            DeleteFileBestEffort(downloadPath);
            DeleteDirectoryBestEffort(stage);
        }
    }

    private async Task DownloadArchiveAsync(
        RegistryPlugin registryPlugin,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        if (
            !Uri.TryCreate(registryPlugin.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("Plugin downloads must use an absolute HTTPS URL.");
        }

        using var response = await _httpClient
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (
            response.Content.Headers.ContentLength is { } contentLength
            && contentLength != registryPlugin.Size
        )
        {
            throw new InvalidDataException(
                $"Plugin archive Content-Length {contentLength} does not match declared size {registryPlugin.Size}."
            );
        }

        var expectedHash = Convert.FromHexString(registryPlugin.Sha256);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var contentStream = await response.Content
            .ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destinationPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            }
        );

        var buffer = new byte[64 * 1024];
        long downloaded = 0;
        while (true)
        {
            var read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            downloaded += read;
            if (downloaded > registryPlugin.Size)
            {
                throw new InvalidDataException(
                    "Plugin archive exceeds its declared download size."
                );
            }

            hash.AppendData(buffer, 0, read);
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Report((double)downloaded / registryPlugin.Size);
        }

        await fileStream.FlushAsync(ct).ConfigureAwait(false);
        if (downloaded != registryPlugin.Size)
        {
            throw new InvalidDataException(
                $"Plugin archive size {downloaded} does not match declared size {registryPlugin.Size}."
            );
        }

        var actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("Plugin archive SHA-256 does not match the registry.");
        }
    }

    private void ExtractAndValidateArchive(
        string archivePath,
        string stage,
        RegistryPlugin registryPlugin,
        CancellationToken ct
    )
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"Plugin archive entry count must be between 1 and {MaxArchiveEntries}."
            );
        }

        var entries = ValidateArchiveEntries(archive, registryPlugin);
        var stagePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage))
            + Path.DirectorySeparatorChar;
        var buffer = new byte[64 * 1024];
        foreach (var entryInfo in entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(
                Path.Join(stage, entryInfo.Path.Replace('/', Path.DirectorySeparatorChar))
            );
            if (!destination.StartsWith(stagePrefix, PathComparison))
            {
                throw new InvalidDataException(
                    $"Plugin archive entry escapes staging: {entryInfo.Entry.FullName}"
                );
            }

            if (entryInfo.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entryInfo.Entry.Open();
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            long written = 0;
            while (true)
            {
                // One entry can be most of the 2 GB budget, so the per-entry check above
                // is not enough on its own.
                ct.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > entryInfo.Entry.Length)
                {
                    throw new InvalidDataException(
                        $"Plugin archive entry expanded beyond its declared size: {entryInfo.Path}"
                    );
                }

                output.Write(buffer, 0, read);
            }

            if (written != entryInfo.Entry.Length)
            {
                throw new InvalidDataException(
                    $"Plugin archive entry size changed during extraction: {entryInfo.Path}"
                );
            }
        }

        ValidateStagedManifest(stage, registryPlugin);
    }

    private static List<ValidatedArchiveEntry> ValidateArchiveEntries(
        ZipArchive archive,
        RegistryPlugin registryPlugin
    )
    {
        var entries = new List<ValidatedArchiveEntry>(archive.Entries.Count);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException(
                    $"Plugin archive contains a symbolic link: {entry.FullName}"
                );
            }

            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var normalized = NormalizeArchivePath(entry.FullName, isDirectory);
            if (!paths.TryAdd(normalized, isDirectory))
            {
                throw new InvalidDataException(
                    $"Plugin archive contains duplicate or case-colliding entries: {entry.FullName}"
                );
            }

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- the
            // LINQ form would enumerate the dictionary through a different enumerator, and the
            // loop body throws with the offending entry rather than producing a value.
            foreach (var existing in paths)
            {
                if (
                    (!existing.Value
                        && normalized.StartsWith(existing.Key + "/", StringComparison.OrdinalIgnoreCase))
                    || (!isDirectory
                        && existing.Key.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
                )
                {
                    throw new InvalidDataException(
                        $"Plugin archive contains a file/directory layout collision: {entry.FullName}"
                    );
                }
            }

            if (
                !isDirectory
                && string.Equals(
                    Path.GetFileName(normalized),
                    "TypeWhisper.PluginSDK.dll",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidDataException(
                    "Plugin archives must not bundle TypeWhisper.PluginSDK.dll."
                );
            }

            ValidateNativeRuntimePath(normalized, registryPlugin.Rid);
            if (!isDirectory)
            {
                checked
                {
                    extractedBytes += entry.Length;
                }

                if (extractedBytes > MaxExtractedBytes)
                {
                    throw new InvalidDataException(
                        $"Plugin archive exceeds the {MaxExtractedBytes}-byte extraction limit."
                    );
                }
            }

            entries.Add(new ValidatedArchiveEntry(entry, normalized, isDirectory));
        }

        if (!paths.TryGetValue(PluginManifest.FileName, out var manifestIsDirectory) || manifestIsDirectory)
        {
            throw new InvalidDataException(
                "Plugin archive must contain exactly one root manifest.json."
            );
        }

        return entries;
    }

    private void ValidateStagedManifest(string stage, RegistryPlugin registryPlugin)
    {
        var manifestPath = Path.Join(stage, PluginManifest.FileName);
        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath),
                    s_jsonOptions
                )
                ?? throw new InvalidDataException("Plugin manifest.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Plugin manifest.json is invalid.", ex);
        }

        if (!string.Equals(manifest.Id, registryPlugin.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Plugin manifest id '{manifest.Id}' does not match registry id '{registryPlugin.Id}'."
            );
        }

        if (
            !string.Equals(manifest.Version, registryPlugin.Version, StringComparison.Ordinal)
            || !AppVersion.IsValidStrict(manifest.Version)
        )
        {
            throw new InvalidDataException(
                $"Plugin manifest version '{manifest.Version}' does not match registry version '{registryPlugin.Version}'."
            );
        }

        if (
            !string.Equals(
                NormalizeOptionalVersion(manifest.MinHostVersion),
                NormalizeOptionalVersion(registryPlugin.MinHostVersion),
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "Plugin manifest minimum host version does not match the registry."
            );
        }

        if (
            !AppVersion.IsHostCompatible(
                manifest.MinHostVersion,
                HostVersion,
                out var incompatibilityReason
            )
        )
        {
            throw new InvalidDataException(
                $"Plugin '{manifest.Id}' is incompatible with this host: {incompatibilityReason}"
            );
        }

        if (
            string.IsNullOrWhiteSpace(manifest.AssemblyName)
            || Path.IsPathRooted(manifest.AssemblyName)
            || manifest.AssemblyName.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]
            ) >= 0
            || !string.Equals(
                Path.GetExtension(manifest.AssemblyName),
                ".dll",
                StringComparison.OrdinalIgnoreCase
            )
            || !File.Exists(Path.Join(stage, manifest.AssemblyName))
        )
        {
            throw new InvalidDataException(
                $"Plugin archive does not contain the declared root assembly '{manifest.AssemblyName}'."
            );
        }
    }

    private void EnsureRegistryEntryInstallable(RegistryPlugin registryPlugin)
    {
        ArgumentNullException.ThrowIfNull(registryPlugin);
        if (!IsSafePluginId(registryPlugin.Id))
        {
            throw new InvalidDataException("Registry plugin id is not a safe directory name.");
        }

        if (!IsCompatible(registryPlugin))
        {
            throw new InvalidDataException(
                $"Registry plugin '{registryPlugin.Id}' is not compatible with {HostPlatform}/{RuntimeRid}/{HostSdkAbi}."
            );
        }

        if (registryPlugin.Size is <= 0 or > MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"Plugin archive size must be between 1 and {MaxArchiveBytes} bytes."
            );
        }

        if (!IsValidSha256(registryPlugin.Sha256))
        {
            throw new InvalidDataException("Registry plugin SHA-256 is invalid.");
        }
    }

    // ReSharper disable once UnusedMember.Global -- public plugin uninstall entry point.
    public async Task UninstallPluginAsync(string pluginId)
    {
        if (!IsSafePluginId(pluginId))
        {
            throw new ArgumentException("Plugin id is not a safe directory name.", nameof(pluginId));
        }

        await _pluginManager.UnloadPluginAsync(pluginId).ConfigureAwait(false);

        var pluginDir = Path.Join(_pluginsRoot, pluginId);
        if (Directory.Exists(pluginDir))
        {
            try
            {
                Directory.Delete(pluginDir, true);
                Trace.WriteLine($"[PluginRegistry] Uninstalled plugin: {pluginId}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[PluginRegistry] Failed to delete directory for {pluginId}: {ex.Message}"
                );
            }
        }
    }

    // ReSharper disable once UnusedMember.Global -- public throttled update-check entry point.
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastUpdateCheck < s_updateCheckInterval)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;
        var registry = await FetchRegistryAsync(ct).ConfigureAwait(false);
        var updatesAvailable = registry
            .Where(p => GetInstallState(p) == PluginInstallState.UpdateAvailable)
            .ToList();

        if (updatesAvailable.Count > 0)
        {
            Trace.WriteLine(
                $"[PluginRegistry] {updatesAvailable.Count} plugin update(s) available"
            );
        }
    }

    public async Task FirstRunAutoInstallAsync(CancellationToken ct = default)
    {
        if (_settings.Current.PluginFirstRunCompleted)
        {
            return;
        }

        Trace.WriteLine(
            "[PluginRegistry] First run detected, auto-installing Linux-compatible registry plugins..."
        );

        // Deliberately no ConfigureAwait(false) in this method: the bootstrap runner calls it
        // on the UI thread and _settings.Save raises SettingsChanged synchronously into
        // settings view models that mutate bound collections without dispatching.
        var (registry, fetchSucceeded) = await FetchRegistryWithOutcomeAsync(ct);
        var anyFailed = !fetchSucceeded;

        foreach (var plugin in registry)
        {
            if (GetInstallState(plugin) != PluginInstallState.NotInstalled)
            {
                continue;
            }

            try
            {
                await InstallPluginAsync(plugin, ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a failed install: don't log one failure per remaining plugin.
                throw;
            }
            catch (Exception ex)
            {
                anyFailed = true;
                Trace.WriteLine(
                    $"[PluginRegistry] Auto-install failed for {plugin.Id}: {ex.Message}"
                );
            }
        }

        if (!anyFailed)
        {
            _settings.Save(_settings.Current with { PluginFirstRunCompleted = true });
        }
    }

    private bool IsCompatible(RegistryPlugin plugin)
    {
        string reason;
        if (!IsSafePluginId(plugin.Id))
        {
            reason = "plugin id is not a safe directory name";
        }
        else if (!string.Equals(plugin.Platform, HostPlatform, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"platform '{plugin.Platform}' is not '{HostPlatform}'";
        }
        else if (!string.Equals(plugin.Rid, RuntimeRid, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"RID '{plugin.Rid}' is not '{RuntimeRid}'";
        }
        else if (!string.Equals(plugin.SdkAbi, HostSdkAbi, StringComparison.Ordinal))
        {
            reason = $"SDK ABI '{plugin.SdkAbi}' is not '{HostSdkAbi}'";
        }
        else if (!AppVersion.IsValidStrict(plugin.Version))
        {
            reason = $"version '{plugin.Version}' is not valid SemVer";
        }
        else if (
            !AppVersion.IsHostCompatible(
                plugin.MinHostVersion,
                HostVersion,
                out var incompatibilityReason
            )
        )
        {
            reason = incompatibilityReason;
        }
        else
        {
            return true;
        }

        Trace.WriteLine(
            $"[PluginRegistry] Excluding incompatible plugin '{plugin.Id}': {reason}"
        );
        return false;
    }

    private static string NormalizeArchivePath(string rawPath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(rawPath) || rawPath.Contains('\0'))
        {
            throw new InvalidDataException("Plugin archive contains an empty or invalid path.");
        }

        var normalized = rawPath.Replace('\\', '/');
        if (
            normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        )
        {
            throw new InvalidDataException(
                $"Plugin archive contains an absolute path: {rawPath}"
            );
        }

        if (isDirectory)
        {
            normalized = normalized.TrimEnd('/');
        }

        var segments = normalized.Split('/');
        if (
            segments.Length == 0
            || segments.Any(segment => segment.Length == 0 || segment is "." or "..")
        )
        {
            throw new InvalidDataException(
                $"Plugin archive contains a traversal or malformed path: {rawPath}"
            );
        }

        return string.Join('/', segments);
    }

    private static void ValidateNativeRuntimePath(string normalizedPath, string declaredRid)
    {
        var segments = normalizedPath.Split('/');
        if (
            segments.Length >= 2
            && string.Equals(segments[0], "runtimes", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(segments[1], declaredRid, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException(
                $"Plugin archive contains undeclared native runtime '{segments[1]}'."
            );
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (unixMode & unixFileTypeMask) == unixSymbolicLink
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsSafePluginId(string id)
    {
        return !string.IsNullOrWhiteSpace(id)
            && id is not ("." or "..")
            && !Path.IsPathRooted(id)
            && id.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    private static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? NormalizeOptionalVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string ResolveRuntimeRid()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm",
            Architecture.X86 => "linux-x86",
            var architecture => $"linux-{architecture.ToString().ToLowerInvariant()}",
        };
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to clean download '{path}': {ex.Message}");
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to clean directory '{path}': {ex.Message}");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record ValidatedArchiveEntry(
        ZipArchiveEntry Entry,
        string Path,
        bool IsDirectory
    );
}
