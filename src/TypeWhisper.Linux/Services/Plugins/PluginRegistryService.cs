using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
/// Fetches the plugin registry from GitHub, manages installation, uninstallation,
/// and update checking for Linux-compatible marketplace plugins.
/// </summary>
public sealed class PluginRegistryService
{
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-win/plugins.json";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Keep Linux on the set already proven by the old bundled-plugin path.
    private static readonly HashSet<string> SupportedPluginIds = new(StringComparer.OrdinalIgnoreCase)
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
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.voxtral",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.obsidian",
        "com.typewhisper.linear",
        "com.typewhisper.openai-compatible"
    };

    private readonly PluginManager _pluginManager;
    private readonly PluginLoader _pluginLoader;
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;

    private List<RegistryPlugin>? _cachedRegistry;
    private DateTime _cacheTimestamp;
    private DateTime _lastUpdateCheck;

    public PluginRegistryService(
        PluginManager pluginManager,
        PluginLoader pluginLoader,
        ISettingsService settings,
        HttpClient? httpClient = null)
    {
        _pluginManager = pluginManager;
        _pluginLoader = pluginLoader;
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Fetches the plugin registry from the remote URL. Results are cached for 5 minutes.
    /// Filters out incompatible host versions and plugins not supported on Linux.
    /// </summary>
    public async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryAsync(CancellationToken ct = default)
    {
        if (_cachedRegistry is not null && DateTime.UtcNow - _cacheTimestamp < CacheDuration)
            return _cachedRegistry;

        try
        {
            var json = await _httpClient.GetStringAsync(RegistryUrl, ct);
            var allPlugins = JsonSerializer.Deserialize<List<RegistryPlugin>>(json, JsonOptions) ?? [];

            var hostVersion = GetHostVersion();
            _cachedRegistry = allPlugins
                .Where(p => SupportedPluginIds.Contains(p.Id))
                .Where(p => IsCompatible(p.MinHostVersion, hostVersion))
                .ToList();
            _cacheTimestamp = DateTime.UtcNow;

            Trace.WriteLine($"[PluginRegistry] Fetched {_cachedRegistry.Count} compatible Linux plugin(s) from registry");
            return _cachedRegistry;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to fetch registry: {ex.Message}");
            return _cachedRegistry ?? [];
        }
    }

    /// <summary>
    /// Determines the install state of a registry plugin by comparing it with locally loaded plugins.
    /// </summary>
    public PluginInstallState GetInstallState(RegistryPlugin registryPlugin)
    {
        var local = _pluginManager.GetPlugin(registryPlugin.Id);
        if (local is null)
            return PluginInstallState.NotInstalled;

        if (Version.TryParse(registryPlugin.Version, out var remoteVer) &&
            Version.TryParse(local.Manifest.Version, out var localVer) &&
            remoteVer > localVer)
        {
            return PluginInstallState.UpdateAvailable;
        }

        return PluginInstallState.Installed;
    }

    /// <summary>
    /// Downloads and installs a plugin from the registry.
    /// </summary>
    public async Task InstallPluginAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var pluginDir = Path.Combine(TypeWhisperEnvironment.PluginsPath, registryPlugin.Id);

        if (_pluginManager.GetPlugin(registryPlugin.Id) is not null)
            await _pluginManager.UnloadPluginAsync(registryPlugin.Id);

        if (Directory.Exists(pluginDir))
            Directory.Delete(pluginDir, recursive: true);

        Directory.CreateDirectory(pluginDir);

        try
        {
            var tempZip = Path.GetTempFileName();
            try
            {
                using var response = await _httpClient.GetAsync(
                    registryPlugin.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
            catch
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
                throw;
            }

            ZipFile.ExtractToDirectory(tempZip, pluginDir, overwriteFiles: true);
            File.Delete(tempZip);

            await _pluginManager.LoadPluginFromDirectoryAsync(pluginDir, activate: true);

            Trace.WriteLine($"[PluginRegistry] Installed plugin: {registryPlugin.Id} v{registryPlugin.Version}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] Failed to install {registryPlugin.Id}: {ex.Message}");

            if (Directory.Exists(pluginDir))
            {
                try { Directory.Delete(pluginDir, recursive: true); }
                catch { }
            }

            throw;
        }
    }

    /// <summary>
    /// Uninstalls a plugin by unloading it and deleting its directory.
    /// </summary>
    public async Task UninstallPluginAsync(string pluginId)
    {
        await _pluginManager.UnloadPluginAsync(pluginId);

        var pluginDir = Path.Combine(TypeWhisperEnvironment.PluginsPath, pluginId);
        if (Directory.Exists(pluginDir))
        {
            try
            {
                Directory.Delete(pluginDir, recursive: true);
                Trace.WriteLine($"[PluginRegistry] Uninstalled plugin: {pluginId}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PluginRegistry] Failed to delete directory for {pluginId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks for available plugin updates. Respects a 24-hour interval.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckInterval)
            return;

        _lastUpdateCheck = DateTime.UtcNow;

        var registry = await FetchRegistryAsync(ct);
        var updatesAvailable = registry
            .Where(p => GetInstallState(p) == PluginInstallState.UpdateAvailable)
            .ToList();

        if (updatesAvailable.Count > 0)
            Trace.WriteLine($"[PluginRegistry] {updatesAvailable.Count} plugin update(s) available");
    }

    /// <summary>
    /// On first run, auto-installs all compatible registry plugins.
    /// Sets the PluginFirstRunCompleted flag to prevent re-running.
    /// </summary>
    public async Task FirstRunAutoInstallAsync(CancellationToken ct = default)
    {
        if (_settings.Current.PluginFirstRunCompleted)
            return;

        Trace.WriteLine("[PluginRegistry] First run detected, auto-installing Linux-compatible registry plugins...");

        try
        {
            var registry = await FetchRegistryAsync(ct);
            foreach (var plugin in registry)
            {
                if (GetInstallState(plugin) == PluginInstallState.NotInstalled)
                {
                    try
                    {
                        await InstallPluginAsync(plugin, ct: ct);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[PluginRegistry] Auto-install failed for {plugin.Id}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginRegistry] First run auto-install failed: {ex.Message}");
        }

        _settings.Save(_settings.Current with { PluginFirstRunCompleted = true });
    }

    private static Version GetHostVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetName().Version ?? new Version(1, 0);
    }

    private static bool IsCompatible(string? minHostVersion, Version hostVersion)
    {
        if (string.IsNullOrEmpty(minHostVersion))
            return true;

        return !Version.TryParse(minHostVersion, out var minVer) || hostVersion >= minVer;
    }
}
