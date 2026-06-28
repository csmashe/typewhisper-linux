using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Fetches the plugin registry from GitHub, manages installation, uninstallation,
///     and update checking for Linux-compatible marketplace plugins.
/// </summary>
public sealed class PluginRegistryService
{
    // Registry JSON is hosted under the Windows repo but shared; filtered to Linux-compatible IDs below.
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-win/plugins.json";
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_updateCheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Restrict to the set already proven by the old bundled-plugin path.
    private static readonly HashSet<string> s_supportedPluginIds = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.file-memory",
        "com.typewhisper.openai",
        "com.typewhisper.openrouter",
        "com.typewhisper.gemini",
        "com.typewhisper.cerebras",
        "com.typewhisper.claude",
        "com.typewhisper.cohere",
        "com.typewhisper.fireworks",
        "com.typewhisper.groq",
        "com.typewhisper.assemblyai",
        "com.typewhisper.deepgram",
        "com.typewhisper.cloudflare-asr",
        "com.typewhisper.gladia",
        "com.typewhisper.speechmatics",
        "com.typewhisper.soniox",
        "com.typewhisper.reson8",
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.voxtral",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.obsidian",
        "com.typewhisper.linear",
        "com.typewhisper.openai-compatible"
    };

    private readonly HttpClient _httpClient;

    // kept injected as a DI/test seam; not consumed in-tree
    // ReSharper disable once NotAccessedField.Local
    private readonly PluginLoader _pluginLoader;

    private readonly PluginManager _pluginManager;
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
    {
        _pluginManager = pluginManager;
        _pluginLoader = pluginLoader;
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryAsync(
        CancellationToken ct = default
    )
    {
        if (_cachedRegistry is not null && DateTime.UtcNow - _cacheTimestamp < s_cacheDuration)
        {
            return _cachedRegistry;
        }

        try
        {
            var json = await _httpClient.GetStringAsync(RegistryUrl, ct);
            var allPlugins =
                JsonSerializer.Deserialize<List<RegistryPlugin>>(json, s_jsonOptions) ?? [];

            var hostVersion = GetHostVersion();
            _cachedRegistry = allPlugins
                .Where(p => s_supportedPluginIds.Contains(p.Id))
                .Where(p => IsCompatible(p.MinHostVersion, hostVersion))
                .ToList();
            _cacheTimestamp = DateTime.UtcNow;

            Trace.WriteLine(
                $"[PluginRegistry] Fetched {_cachedRegistry.Count} compatible Linux plugin(s) from registry"
            );
            return _cachedRegistry;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to fetch registry: {ex.Message}");
            return _cachedRegistry ?? [];
        }
    }

    public PluginInstallState GetInstallState(RegistryPlugin registryPlugin)
    {
        var local = _pluginManager.GetPlugin(registryPlugin.Id);
        if (local is null)
        {
            return PluginInstallState.NotInstalled;
        }

        if (
            Version.TryParse(registryPlugin.Version, out var remoteVer)
            && Version.TryParse(local.Manifest.Version, out var localVer)
            && remoteVer > localVer
        )
        {
            return PluginInstallState.UpdateAvailable;
        }

        return PluginInstallState.Installed;
    }

    private async Task InstallPluginAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default
    )
    {
        var pluginDir = Path.Join(TypeWhisperEnvironment.PluginsPath, registryPlugin.Id);

        if (_pluginManager.GetPlugin(registryPlugin.Id) is not null)
        {
            await _pluginManager.UnloadPluginAsync(registryPlugin.Id);
        }

        if (Directory.Exists(pluginDir))
        {
            Directory.Delete(pluginDir, true);
        }

        Directory.CreateDirectory(pluginDir);

        var tempZip = Path.GetTempFileName();
        try
        {
            // Scope the download streams so the temp file is closed before we
            // extract from it; the surrounding finally deletes tempZip on every
            // exit path (download, extract, or load failure included).
            using (var response = await _httpClient.GetAsync(
                       registryPlugin.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? registryPlugin.Size;
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[8192];
                long bytesRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRead += read;
                    progress?.Report(totalBytes > 0 ? (double)bytesRead / totalBytes : 0);
                }
            }

            // ReSharper disable once MethodHasAsyncOverloadWithCancellation -- synchronous extraction is intentional; the async overload would change cancellation semantics (partial extract on cancel) for a small local plugin zip
            ZipFile.ExtractToDirectory(tempZip, pluginDir, true);

            await _pluginManager.LoadPluginFromDirectoryAsync(pluginDir, true);

            Trace.WriteLine(
                $"[PluginRegistry] Installed plugin: {registryPlugin.Id} v{registryPlugin.Version}"
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginRegistry] Failed to install {registryPlugin.Id}: {ex.Message}"
            );

            if (!Directory.Exists(pluginDir))
            {
                throw;
            }

            try
            {
                Directory.Delete(pluginDir, true);
            }
            catch
            {
                // Best-effort cleanup of the partial install directory; the original failure is rethrown below.
            }

            throw;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try
                {
                    File.Delete(tempZip);
                }
                catch
                {
                    // Best-effort temp cleanup; nothing else depends on it.
                }
            }
        }
    }

    // ReSharper disable once UnusedMember.Global  public API surface (plugin uninstall entry point); not currently called in-tree
    public async Task UninstallPluginAsync(string pluginId)
    {
        await _pluginManager.UnloadPluginAsync(pluginId);

        var pluginDir = Path.Join(TypeWhisperEnvironment.PluginsPath, pluginId);
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

    /// <summary>
    ///     Checks for plugin updates, throttled to one network probe per 24 h to avoid hammering
    ///     the registry endpoint on repeated launches.
    /// </summary>
    // ReSharper disable once UnusedMember.Global  public API surface (throttled plugin update check); not currently called in-tree
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastUpdateCheck < s_updateCheckInterval)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;

        var registry = await FetchRegistryAsync(ct);
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

    /// <summary>
    ///     First-launch bootstrap: installs all compatible registry plugins so the app is usable
    ///     out of the box. Guarded by PluginFirstRunCompleted so it won't re-run after user uninstalls.
    /// </summary>
    public async Task FirstRunAutoInstallAsync(CancellationToken ct = default)
    {
        if (_settings.Current.PluginFirstRunCompleted)
        {
            return;
        }

        Trace.WriteLine(
            "[PluginRegistry] First run detected, auto-installing Linux-compatible registry plugins..."
        );

        // Only mark first-run bootstrap complete once everything actually
        // installed. A failed fetch (e.g. offline) or a failed plugin install
        // must leave the flag clear so the next launch retries.
        var anyFailed = false;
        try
        {
            var registry = await FetchRegistryAsync(ct);
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
                catch (Exception ex)
                {
                    anyFailed = true;
                    Trace.WriteLine(
                        $"[PluginRegistry] Auto-install failed for {plugin.Id}: {ex.Message}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            anyFailed = true;
            Trace.WriteLine($"[PluginRegistry] First run auto-install failed: {ex.Message}");
        }

        if (!anyFailed)
        {
            _settings.Save(_settings.Current with { PluginFirstRunCompleted = true });
        }
    }

    private static Version GetHostVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetName().Version ?? new Version(1, 0);
    }

    private static bool IsCompatible(string? minHostVersion, Version hostVersion)
    {
        if (string.IsNullOrEmpty(minHostVersion))
        {
            return true;
        }

        return !Version.TryParse(minHostVersion, out var minVer) || hostVersion >= minVer;
    }
}