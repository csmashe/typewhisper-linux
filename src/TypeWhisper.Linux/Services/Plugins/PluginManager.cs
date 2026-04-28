using System.Collections.Concurrent;
using System.Diagnostics;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
/// Central plugin registry and lifecycle manager. Discovers plugins, maintains
/// enabled/disabled state, and provides typed capability indices for LLM providers,
/// transcription engines, and post-processors.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginEventBus _eventBus;
    private readonly IActiveWindowService _activeWindow;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly string[] _searchDirectories;

    private readonly List<LoadedPlugin> _allPlugins = [];
    private readonly Dictionary<string, PluginHostServices> _hostServices = [];
    private readonly HashSet<string> _activatedPlugins = [];
    private readonly ConcurrentDictionary<string, Task<bool>> _activationTasks = new();
    private readonly object _lock = new();

    private List<ILlmProviderPlugin> _llmProviders = [];
    private List<ITranscriptionEnginePlugin> _transcriptionEngines = [];
    private List<IPostProcessorPlugin> _postProcessors = [];
    private List<IActionPlugin> _actionPlugins = [];
    private List<ITtsProviderPlugin> _ttsProviders = [];

    public PluginManager(
        PluginLoader loader,
        PluginEventBus eventBus,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        ISettingsService settings)
        : this(loader, eventBus, activeWindow, profiles, settings, [TypeWhisperEnvironment.PluginsPath])
    {
    }

    internal PluginManager(
        PluginLoader loader,
        PluginEventBus eventBus,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        ISettingsService settings,
        IEnumerable<string> searchDirectories)
    {
        _loader = loader;
        _eventBus = eventBus;
        _activeWindow = activeWindow;
        _profiles = profiles;
        _settings = settings;
        _searchDirectories = searchDirectories.ToArray();
    }

    public IReadOnlyList<LoadedPlugin> AllPlugins
    {
        get { lock (_lock) return [.. _allPlugins]; }
    }

    public IReadOnlyList<ILlmProviderPlugin> LlmProviders
    {
        get { lock (_lock) return [.. _llmProviders]; }
    }

    public IReadOnlyList<ITranscriptionEnginePlugin> TranscriptionEngines
    {
        get { lock (_lock) return [.. _transcriptionEngines]; }
    }

    public IReadOnlyList<IPostProcessorPlugin> PostProcessors
    {
        get { lock (_lock) return [.. _postProcessors]; }
    }

    public IReadOnlyList<IActionPlugin> ActionPlugins
    {
        get { lock (_lock) return [.. _actionPlugins]; }
    }

    public IReadOnlyList<ITtsProviderPlugin> TtsProviders
    {
        get { lock (_lock) return [.. _ttsProviders]; }
    }

    public IReadOnlyList<PluginLoadFailure> LoadFailures => _loader.LastLoadFailures;

    public IReadOnlyList<T> GetPlugins<T>() where T : class
    {
        lock (_lock)
            return _allPlugins
                .Where(p => _activatedPlugins.Contains(p.Manifest.Id) && p.Instance is T)
                .Select(p => (T)p.Instance!)
                .ToList();
    }

    public event EventHandler? PluginStateChanged;

    public PluginEventBus EventBus => _eventBus;

    public async Task InitializeAsync()
    {
        var discovered = _loader.DiscoverAndLoad(_searchDirectories);

        lock (_lock)
        {
            _allPlugins.Clear();
            _allPlugins.AddRange(discovered);
        }

        Trace.WriteLine($"[PluginManager] Discovered {discovered.Count} plugin(s)");

        var enabledState = _settings.Current.PluginEnabledState;

        foreach (var plugin in discovered)
        {
            var isEnabled = !enabledState.TryGetValue(plugin.Manifest.Id, out var state) || state;

            if (isEnabled)
            {
                await ActivatePluginAsync(plugin);
            }
        }

        RebuildCapabilityIndices();
        await MigrateApiKeysAsync();
    }

    public async Task EnablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
        {
            Trace.WriteLine($"[PluginManager] Plugin not found: {pluginId}");
            return;
        }

        // Short-circuit if already activated — otherwise a second call after
        // the first activation task was removed from _activationTasks would
        // run ActivatePluginAsync again and double-activate.
        lock (_lock)
        {
            if (_activatedPlugins.Contains(pluginId))
            {
                PersistEnabledState(pluginId, true);
                return;
            }
        }

        // Serialize activation per plugin so concurrent callers share one
        // activation Task instead of both passing the Contains check and
        // double-activating.
        var activation = _activationTasks.GetOrAdd(pluginId, _ => ActivatePluginAsync(plugin));
        bool success;
        try
        {
            success = await activation;
        }
        finally
        {
            _activationTasks.TryRemove(new KeyValuePair<string, Task<bool>>(pluginId, activation));
        }

        if (!success) return;

        RebuildCapabilityIndices();
        PersistEnabledState(pluginId, true);
    }

    public async Task DisablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
            return;

        bool wasActivated;
        lock (_lock)
        {
            wasActivated = _activatedPlugins.Contains(pluginId);
        }

        if (!wasActivated)
        {
            PersistEnabledState(pluginId, false);
            return;
        }

        if (!await DeactivatePluginAsync(plugin))
            return;

        RebuildCapabilityIndices();
        PersistEnabledState(pluginId, false);
    }

    public bool IsEnabled(string pluginId)
    {
        lock (_lock)
        {
            return _activatedPlugins.Contains(pluginId);
        }
    }

    public LoadedPlugin? GetPlugin(string pluginId)
    {
        lock (_lock)
        {
            return _allPlugins.FirstOrDefault(p => p.Manifest.Id == pluginId);
        }
    }

    private async Task<bool> ActivatePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            var hostServices = new PluginHostServices(
                plugin.Manifest.Id, plugin.PluginDirectory,
                _activeWindow, _eventBus, _profiles,
                onCapabilitiesChanged: () =>
                {
                    RebuildCapabilityIndices();
                    PluginStateChanged?.Invoke(this, EventArgs.Empty);
                });

            await plugin.Instance.ActivateAsync(hostServices);

            lock (_lock)
            {
                _hostServices[plugin.Manifest.Id] = hostServices;
                _activatedPlugins.Add(plugin.Manifest.Id);
            }

            Trace.WriteLine($"[PluginManager] Activated plugin: {plugin.Manifest.Id}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] Failed to activate plugin {plugin.Manifest.Id}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DeactivatePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            await plugin.Instance.DeactivateAsync();

            lock (_lock)
            {
                _hostServices.Remove(plugin.Manifest.Id);
                _activatedPlugins.Remove(plugin.Manifest.Id);
            }

            Trace.WriteLine($"[PluginManager] Deactivated plugin: {plugin.Manifest.Id}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] Failed to deactivate plugin {plugin.Manifest.Id}: {ex.Message}");
            return false;
        }
    }

    private void RebuildCapabilityIndices()
    {
        lock (_lock)
        {
            var activePlugins = _allPlugins
                .Where(p => _activatedPlugins.Contains(p.Manifest.Id))
                .Select(p => p.Instance)
                .ToList();

            _llmProviders = activePlugins.OfType<ILlmProviderPlugin>().ToList();
            _transcriptionEngines = activePlugins.OfType<ITranscriptionEnginePlugin>().ToList();
            _postProcessors = activePlugins.OfType<IPostProcessorPlugin>()
                .OrderBy(p => p.Priority)
                .ToList();
            _actionPlugins = activePlugins.OfType<IActionPlugin>().ToList();
            _ttsProviders = activePlugins.OfType<ITtsProviderPlugin>().ToList();
        }

        PluginStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UnloadPluginAsync(string pluginId)
    {
        LoadedPlugin? plugin;
        lock (_lock)
        {
            plugin = _allPlugins.FirstOrDefault(p => p.Manifest.Id == pluginId);
        }

        if (plugin is null)
            return;

        bool wasActivated;
        lock (_lock)
        {
            wasActivated = _activatedPlugins.Contains(pluginId);
        }

        if (wasActivated)
            await DeactivatePluginAsync(plugin);

        // Always run Unload, even if Dispose throws — otherwise the
        // collectible ALC stays rooted and its native deps aren't freed.
        try
        {
            plugin.Instance.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] Dispose failed for {pluginId}: {ex.Message}");
        }

        try
        {
            plugin.LoadContext.Unload();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] LoadContext.Unload failed for {pluginId}: {ex.Message}");
        }

        lock (_lock)
        {
            _allPlugins.RemoveAll(p => p.Manifest.Id == pluginId);
        }

        RebuildCapabilityIndices();
    }

    public async Task LoadPluginFromDirectoryAsync(string pluginDirectory, bool activate)
    {
        var plugin = _loader.LoadPlugin(pluginDirectory);
        if (plugin is null)
        {
            Trace.WriteLine($"[PluginManager] Failed to load plugin from {pluginDirectory}");
            return;
        }

        // Tear down any existing plugin with the same Id through the normal
        // lifecycle so we don't leak its host services or load context.
        bool hasExisting;
        lock (_lock)
        {
            hasExisting = _allPlugins.Any(p => p.Manifest.Id == plugin.Manifest.Id);
        }

        if (hasExisting)
            await UnloadPluginAsync(plugin.Manifest.Id);

        lock (_lock)
        {
            _allPlugins.Add(plugin);
        }

        if (activate)
        {
            await ActivatePluginAsync(plugin);
            PersistEnabledState(plugin.Manifest.Id, true);
        }

        RebuildCapabilityIndices();
    }

    private void PersistEnabledState(string pluginId, bool enabled)
    {
        try
        {
            var current = _settings.Current;
            var updatedState = new Dictionary<string, bool>(current.PluginEnabledState)
            {
                [pluginId] = enabled
            };

            _settings.Save(current with { PluginEnabledState = updatedState });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] Failed to persist enabled state for {pluginId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates legacy GroqApiKey/OpenAiApiKey from AppSettings to plugin secrets.
    /// On a fresh Linux install these fields are typically empty so this is a no-op.
    /// </summary>
    private async Task MigrateApiKeysAsync()
    {
        var settings = _settings.Current;
        var migratedGroq = false;
        var migratedOpenAi = false;

        if (!string.IsNullOrEmpty(settings.GroqApiKey))
            migratedGroq = await MigrateKeyToPluginAsync("com.typewhisper.groq", "api-key", settings.GroqApiKey);

        if (!string.IsNullOrEmpty(settings.OpenAiApiKey))
            migratedOpenAi = await MigrateKeyToPluginAsync("com.typewhisper.openai", "api-key", settings.OpenAiApiKey);

        if (migratedGroq || migratedOpenAi)
        {
            var current = _settings.Current;
            _settings.Save(current with
            {
                GroqApiKey = migratedGroq ? "" : current.GroqApiKey,
                OpenAiApiKey = migratedOpenAi ? "" : current.OpenAiApiKey
            });
        }
    }

    private async Task<bool> MigrateKeyToPluginAsync(string pluginId, string secretKey, string encryptedValue)
    {
        PluginHostServices? hostServices;
        lock (_lock)
        {
            _hostServices.TryGetValue(pluginId, out hostServices);
        }

        if (hostServices is null)
            return false;

        try
        {
            var decrypted = ApiKeyProtection.Decrypt(encryptedValue);
            if (string.IsNullOrEmpty(decrypted))
                return false;

            await hostServices.StoreSecretAsync(secretKey, decrypted);
            Trace.WriteLine($"[PluginManager] Migrated API key to plugin: {pluginId}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginManager] Failed to migrate API key for {pluginId}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        List<LoadedPlugin> plugins;
        HashSet<string> activated;
        lock (_lock)
        {
            plugins = [.. _allPlugins];
            activated = [.. _activatedPlugins];
        }

        foreach (var plugin in plugins)
        {
            try
            {
                if (activated.Contains(plugin.Manifest.Id))
                {
                    plugin.Instance.DeactivateAsync().GetAwaiter().GetResult();
                }

                plugin.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PluginManager] Error disposing plugin {plugin.Manifest.Id}: {ex.Message}");
            }

            try
            {
                plugin.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PluginManager] Error unloading context for {plugin.Manifest.Id}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _allPlugins.Clear();
            _hostServices.Clear();
            _activatedPlugins.Clear();
            _llmProviders.Clear();
            _transcriptionEngines.Clear();
            _postProcessors.Clear();
            _actionPlugins.Clear();
            _ttsProviders.Clear();
        }
    }

    public ITtsProviderPlugin? GetTtsProvider(string providerId)
    {
        lock (_lock)
        {
            return _ttsProviders.FirstOrDefault(provider =>
                string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
