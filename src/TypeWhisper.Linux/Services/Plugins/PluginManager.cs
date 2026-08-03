using System.Collections.Concurrent;
using System.Diagnostics;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Central plugin registry and lifecycle manager. Discovers plugins, maintains
///     enabled/disabled state, and provides typed capability indices for LLM providers,
///     transcription engines, and post-processors.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private static readonly TimeSpan s_defaultPluginShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _activatedPlugins = [];
    private readonly ConcurrentDictionary<string, Task<bool>> _activationTasks = new();
    private readonly IActiveWindowService _activeWindow;

    private readonly List<LoadedPlugin> _allPlugins = [];
    private readonly Dictionary<string, PluginHostServices> _hostServices = [];
    private readonly PluginLoader _loader;
    private readonly Lock _lock = new();
    private readonly IProfileService _profiles;
    private readonly string[] _searchDirectories;
    private readonly ISettingsService _settings;
    private readonly TimeSpan _pluginShutdownTimeout;
    private readonly IErrorLogService? _errorLog;
    private List<IActionPlugin> _actionPlugins = [];

    // Debounce guard for on-demand model re-polls (triggered when a dropdown opens).
    private bool _isRefreshingModels;
    private DateTime _lastModelRefresh = DateTime.MinValue;

    private List<ILlmProviderRole> _llmProviders = [];
    private List<IPostProcessorPlugin> _postProcessors = [];
    private List<ITranscriptionEngineRole> _transcriptionEngines = [];
    private List<ITtsProviderPlugin> _ttsProviders = [];

    public PluginManager(
        PluginLoader loader,
        PluginEventBus eventBus,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        ISettingsService settings,
        IErrorLogService? errorLog = null
    )
        : this(
            loader,
            eventBus,
            activeWindow,
            profiles,
            settings,
            [TypeWhisperEnvironment.PluginsPath],
            errorLog
        )
    {
    }

    internal PluginManager(
        PluginLoader loader,
        PluginEventBus eventBus,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        ISettingsService settings,
        IEnumerable<string> searchDirectories,
        IErrorLogService? errorLog = null,
        TimeSpan? pluginShutdownTimeout = null
    )
    {
        _loader = loader;
        EventBus = eventBus;
        _activeWindow = activeWindow;
        _profiles = profiles;
        _settings = settings;
        _searchDirectories = searchDirectories.ToArray();
        _errorLog = errorLog;
        _pluginShutdownTimeout =
            pluginShutdownTimeout ?? s_defaultPluginShutdownTimeout;
        if (_pluginShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pluginShutdownTimeout),
                "The plugin shutdown timeout must be greater than zero."
            );
        }
    }

    public IReadOnlyList<LoadedPlugin> AllPlugins
    {
        get
        {
            lock (_lock)
            {
                return [.. _allPlugins];
            }
        }
    }

    public IReadOnlyList<ILlmProviderRole> LlmProviders
    {
        get
        {
            lock (_lock)
            {
                return [.. _llmProviders];
            }
        }
    }

    public IReadOnlyList<ITranscriptionEngineRole> TranscriptionEngines
    {
        get
        {
            lock (_lock)
            {
                return [.. _transcriptionEngines];
            }
        }
    }

    public IReadOnlyList<IPostProcessorPlugin> PostProcessors
    {
        get
        {
            lock (_lock)
            {
                return [.. _postProcessors];
            }
        }
    }

    public IReadOnlyList<IActionPlugin> ActionPlugins
    {
        get
        {
            lock (_lock)
            {
                return [.. _actionPlugins];
            }
        }
    }

    public IReadOnlyList<ITtsProviderPlugin> TtsProviders
    {
        get
        {
            lock (_lock)
            {
                return [.. _ttsProviders];
            }
        }
    }

    public IReadOnlyList<PluginLoadFailure> LoadFailures => _loader.LastLoadFailures;

    public PluginEventBus EventBus { get; }

    public void Dispose()
    {
        List<LoadedPlugin> plugins;
        HashSet<string> activated;
        lock (_lock)
        {
            plugins = [.. _allPlugins];
            activated = [.. _activatedPlugins];
        }

        // One budget for the whole pass: a per-plugin timeout multiplies by the plugin count.
        var shutdownBudget = Stopwatch.StartNew();

        foreach (var plugin in plugins)
        {
            // Dispose is synchronous and can't be canceled. A hostile plugin can strand this
            // worker past the deadline; bounded shutdown accepts that leaked thread.
            var shutdownTask = Task.Factory.StartNew(
                () => ShutdownPlugin(plugin, activated.Contains(plugin.Manifest.Id)),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );

            AwaitPluginShutdown(
                shutdownTask,
                plugin.Manifest.Id,
                _pluginShutdownTimeout - shutdownBudget.Elapsed
            );

            try
            {
                plugin.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[PluginManager] Error unloading context for {plugin.Manifest.Id}: {ex.Message}"
                );
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

    private void AwaitPluginShutdown(Task shutdownTask, string pluginId, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            Trace.WriteLine(
                "[PluginManager] Shutdown budget of "
                    + $"{_pluginShutdownTimeout.TotalSeconds:0.###} seconds is spent; "
                    + $"not waiting for plugin {pluginId}"
            );
            ObserveLateShutdown(shutdownTask, pluginId);
            return;
        }

        var completedTask = Task.WhenAny(shutdownTask, Task.Delay(remaining))
            .GetAwaiter()
            .GetResult();

        if (completedTask != shutdownTask)
        {
            Trace.WriteLine(
                $"[PluginManager] Timed out shutting down plugin {pluginId} "
                    + $"after {remaining.TotalSeconds:0.###} seconds"
            );
            ObserveLateShutdown(shutdownTask, pluginId);
            return;
        }

        try
        {
            shutdownTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Error shutting down plugin {pluginId}: {ex.Message}"
            );
        }
    }

    private void ShutdownPlugin(LoadedPlugin plugin, bool deactivate)
    {
        if (deactivate)
        {
            try
            {
                var deactivationTask = plugin.Instance.DeactivateAsync();
                var completedTask = Task.WhenAny(
                        deactivationTask,
                        Task.Delay(_pluginShutdownTimeout)
                    )
                    .GetAwaiter()
                    .GetResult();

                if (completedTask == deactivationTask)
                {
                    deactivationTask.GetAwaiter().GetResult();
                }
                else
                {
                    Trace.WriteLine(
                        $"[PluginManager] Timed out deactivating plugin {plugin.Manifest.Id} "
                            + $"after {_pluginShutdownTimeout.TotalSeconds:0.###} seconds"
                    );

                    // Ordering guarantee: deactivate and dispose never run concurrently for the
                    // same plugin. Disposing now would race the still-running deactivation, so
                    // Dispose is deferred to a continuation that fires once it completes —
                    // forfeited entirely if it never does, acceptable since the host is exiting.
                    ObserveLateDeactivationThenDispose(deactivationTask, plugin);
                    return;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[PluginManager] Error deactivating plugin {plugin.Manifest.Id}: {ex.Message}"
                );
            }
        }

        try
        {
            plugin.Instance.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Error disposing plugin {plugin.Manifest.Id}: {ex.Message}"
            );
        }
    }

    private static void ObserveLateDeactivationThenDispose(Task deactivationTask, LoadedPlugin plugin)
    {
        var pluginId = plugin.Manifest.Id;
        _ = deactivationTask.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    Trace.WriteLine(
                        $"[PluginManager] Deactivation for plugin {pluginId} faulted after timeout: "
                            + completedTask.Exception!.GetBaseException().Message
                    );
                }
                else if (completedTask.IsCanceled)
                {
                    Trace.WriteLine(
                        $"[PluginManager] Deactivation for plugin {pluginId} was canceled after timeout"
                    );
                }
                else
                {
                    Trace.WriteLine(
                        $"[PluginManager] Deactivation for plugin {pluginId} completed after timeout"
                    );
                }

                try
                {
                    plugin.Instance.Dispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[PluginManager] Error disposing plugin {pluginId}: {ex.Message}"
                    );
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static void ObserveLateShutdown(Task shutdownTask, string pluginId)
    {
        _ = shutdownTask.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    Trace.WriteLine(
                        $"[PluginManager] Shutdown for plugin {pluginId} faulted after timeout: "
                            + completedTask.Exception!.GetBaseException().Message
                    );
                }
                else
                {
                    Trace.WriteLine(
                        $"[PluginManager] Shutdown for plugin {pluginId} completed after timeout"
                    );
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    public IReadOnlyList<T> GetPlugins<T>()
        where T : class
    {
        lock (_lock)
        {
            return _allPlugins
                .Where(p => _activatedPlugins.Contains(p.Manifest.Id) && p.Instance is T)
                .Select(p => (T)p.Instance)
                .ToList();
        }
    }

    public async Task InitializeAsync()
    {
        var discovered = _loader.DiscoverAndLoad(_searchDirectories);

        lock (_lock)
        {
            _allPlugins.Clear();
            _allPlugins.AddRange(discovered);
        }

        Trace.WriteLine($"[PluginManager] Discovered {discovered.Count} plugin(s)");

        // Surface plugins that were present but couldn't be loaded (bad manifest, missing
        // assembly, constructor threw) — otherwise they silently disappear from the UI.
        foreach (var failure in _loader.LastLoadFailures)
        {
            _errorLog?.AddEntry(
                $"Plugin failed to load from '{failure.PluginDirectory}': {failure.Message}",
                ErrorCategory.Plugin
            );
        }

        var enabledState = _settings.Current.PluginEnabledState;

        foreach (var plugin in discovered)
        {
            // Honor saved choice; otherwise default-enable plugins whose metadata
            // marks them local-only.
            var isEnabled = enabledState.TryGetValue(plugin.Manifest.Id, out var state)
                ? state
                : IsEnabledByDefault(plugin);

            if (isEnabled)
            {
                await ActivatePluginAsync(plugin);
            }
        }

        RebuildCapabilityIndices();
        await MigrateApiKeysAsync();
    }

    internal static bool IsEnabledByDefault(LoadedPlugin plugin)
    {
        return plugin.Metadata.NetworkAccess == PluginNetworkAccess.Local;
    }

    public async Task EnablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
        {
            Trace.WriteLine($"[PluginManager] Plugin not found: {pluginId}");
            return;
        }

        // Short-circuit if already activated to prevent double-activation after the task
        // is removed from _activationTasks. Serialize per plugin so concurrent callers
        // share one Task rather than both passing the Contains check.
        lock (_lock)
        {
            if (_activatedPlugins.Contains(pluginId))
            {
                PersistEnabledState(pluginId, true);
                return;
            }
        }

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

        if (!success)
        {
            return;
        }

        RebuildCapabilityIndices();
        PersistEnabledState(pluginId, true);
    }

    public async Task DisablePluginAsync(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin is null)
        {
            return;
        }

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
        {
            return;
        }

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

    public async Task UnloadPluginAsync(string pluginId)
    {
        LoadedPlugin? plugin;
        lock (_lock)
        {
            plugin = _allPlugins.FirstOrDefault(p => p.Manifest.Id == pluginId);
        }

        if (plugin is null)
        {
            return;
        }

        bool wasActivated;
        lock (_lock)
        {
            wasActivated = _activatedPlugins.Contains(pluginId);
        }

        if (wasActivated)
        {
            await DeactivatePluginAsync(plugin);
        }

        // Always unload even if Dispose throws — otherwise the collectible ALC stays rooted
        // and native deps aren't freed.
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
            Trace.WriteLine(
                $"[PluginManager] LoadContext.Unload failed for {pluginId}: {ex.Message}"
            );
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

        // Unload any existing plugin with the same Id to avoid leaking host services or load context.
        bool hasExisting;
        lock (_lock)
        {
            hasExisting = _allPlugins.Any(p => p.Manifest.Id == plugin.Manifest.Id);
        }

        if (hasExisting)
        {
            await UnloadPluginAsync(plugin.Manifest.Id);
        }

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

    public ITtsProviderPlugin? GetTtsProvider(string providerId)
    {
        lock (_lock)
        {
            return _ttsProviders.FirstOrDefault(provider =>
                string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    /// <summary>
    ///     Re-polls activated <see cref="IModelCatalogProvider" /> plugins so newly added
    ///     models (e.g. a freshly pulled Ollama model) appear without the user hitting "Validate".
    ///     Uses the narrow model-catalog contract rather than <c>ValidateAsync</c>, which can have
    ///     heavy/irreversible side effects (e.g. downloading TTS assets). Call when a model dropdown opens.
    ///     Debounced and re-entrancy-guarded; each provider gets a 10 s timeout; failures are logged.
    /// </summary>
    public async Task RefreshProviderModelsAsync()
    {
        // Claim under the lock so concurrent callers don't both pass the guard.
        // The lock only guards this cheap check, not the awaits below.
        lock (_lock)
        {
            if (_isRefreshingModels)
            {
                return;
            }

            if (DateTime.UtcNow - _lastModelRefresh < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _isRefreshingModels = true;
        }

        try
        {
            List<IModelCatalogProvider> providers;
            lock (_lock)
            {
                providers = _allPlugins
                    .Where(p => _activatedPlugins.Contains(p.Manifest.Id))
                    .Select(p => p.Instance)
                    // ReSharper disable once SuspiciousTypeConversion.Global -- plugin instances are loaded from external assemblies (AssemblyLoadContext) that implement this capability interface; the cross-assembly implementer is not visible in-solution.
                    .OfType<IModelCatalogProvider>()
                    .ToList();
            }

            foreach (var provider in providers)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await provider.RefreshModelCatalogAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    // Best effort — one failing provider must not block the rest.
                    Trace.WriteLine(
                        $"[PluginManager] Model-catalog refresh failed for "
                        + $"{provider.GetType().Name}: {ex.Message}"
                    );
                }
            }

            lock (_lock)
            {
                _lastModelRefresh = DateTime.UtcNow;
            }
        }
        finally
        {
            lock (_lock)
            {
                _isRefreshingModels = false;
            }
        }
    }

    /// <summary>
    ///     Raised when the active plugins or their capabilities change. This event may be raised
    ///     on any thread; UI subscribers are responsible for marshalling to the UI thread.
    /// </summary>
    public event EventHandler? PluginStateChanged;

    private async Task<bool> ActivatePluginAsync(LoadedPlugin plugin)
    {
        try
        {
            var hostServices = new PluginHostServices(
                plugin.Manifest.Id,
                plugin.PluginDirectory,
                _activeWindow,
                EventBus,
                _profiles,
                _settings,
                RebuildCapabilityIndices,
                _errorLog,
                ResolveErrorCategory(plugin),
                plugin.Manifest.Name,
                _loader.PluginDataRoot
            );

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
            Trace.WriteLine(
                $"[PluginManager] Failed to activate plugin {plugin.Manifest.Id}: {ex.Message}"
            );
            _errorLog?.AddEntry(
                $"Plugin '{plugin.Manifest.Name}' failed to activate: {ex.Message}",
                ErrorCategory.Plugin
            );
            return false;
        }
    }

    // Same normalized categories as the UI (Transcription, then Llm, take priority).
    // Legacy manifests that normalized to Unknown fall back to the instance's
    // capability interfaces instead of the generic bucket.
    private static string ResolveErrorCategory(LoadedPlugin plugin)
    {
        if (plugin.Metadata.Categories.Contains(PluginCategory.Transcription))
        {
            return ErrorCategory.Transcription;
        }

        if (plugin.Metadata.Categories.Contains(PluginCategory.Llm))
        {
            return ErrorCategory.Prompt;
        }

        return plugin.Instance switch
        {
            ITranscriptionEnginePlugin => ErrorCategory.Transcription,
            ILlmProviderPlugin => ErrorCategory.Prompt,
            _ => ErrorCategory.Plugin,
        };
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
            Trace.WriteLine(
                $"[PluginManager] Failed to deactivate plugin {plugin.Manifest.Id}: {ex.Message}"
            );
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

            // Fold in extra provider/engine roles contributed by a single plugin
            // (e.g. OpenAI-compatible profiles), then de-dup by selection ID so a
            // role and the plugin's own default never collide. GroupBy().First()
            // keeps the first occurrence — the plugin's primary role is enumerated
            // before its additional roles. Resolve and validate every effective ID
            // before grouping so one malformed external role cannot poison the rebuild.
            _llmProviders = ValidLlmProviders(
                    activePlugins
                        .OfType<ILlmProviderPlugin>()
                        .Concat(
                            activePlugins
                                // ReSharper disable once SuspiciousTypeConversion.Global -- plugin instances are loaded from external assemblies (AssemblyLoadContext) that implement this capability interface; the cross-assembly implementer is not visible in-solution.
                                .OfType<IAdditionalLlmProvidersProvider>()
                                .SelectMany(SafeAdditionalLlmProviders)
                        )
                )
                .GroupBy(entry => entry.SelectionId, StringComparer.Ordinal)
                .Select(group => group.First().Provider)
                .ToList();
            _transcriptionEngines = ValidTranscriptionEngines(
                    activePlugins
                        .OfType<ITranscriptionEnginePlugin>()
                        .Concat(
                            activePlugins
                                // ReSharper disable once SuspiciousTypeConversion.Global -- plugin instances are loaded from external assemblies (AssemblyLoadContext) that implement this capability interface; the cross-assembly implementer is not visible in-solution.
                                .OfType<IAdditionalTranscriptionEnginesProvider>()
                                .SelectMany(SafeAdditionalTranscriptionEngines)
                        )
                )
                .GroupBy(entry => entry.SelectionId, StringComparer.Ordinal)
                .Select(group => group.First().Provider)
                .ToList();
            _postProcessors = activePlugins
                .OfType<IPostProcessorPlugin>()
                .OrderBy(p => p.Priority)
                .ToList();
            _actionPlugins = activePlugins.OfType<IActionPlugin>().ToList();
            _ttsProviders = activePlugins.OfType<ITtsProviderPlugin>().ToList();
        }

        // Raise outside _lock to avoid deadlock if a handler calls back into PluginManager.
        PluginStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<(ILlmProviderRole Provider, string SelectionId)> ValidLlmProviders(
        IEnumerable<ILlmProviderRole> providers
    )
    {
        foreach (var provider in providers)
        {
            string selectionId;
            try
            {
                selectionId = provider.GetLlmSelectionId();
            }
            catch (Exception ex)
            {
                LogInvalidSelectionId(
                    "LLM provider",
                    provider,
                    ex,
                    ErrorCategory.Prompt
                );
                continue;
            }

            yield return (provider, selectionId);
        }
    }

    private IEnumerable<(
        ITranscriptionEngineRole Provider,
        string SelectionId
    )> ValidTranscriptionEngines(IEnumerable<ITranscriptionEngineRole> providers)
    {
        foreach (var provider in providers)
        {
            string selectionId;
            try
            {
                selectionId = provider.GetTranscriptionSelectionId();
            }
            catch (Exception ex)
            {
                LogInvalidSelectionId(
                    "transcription engine",
                    provider,
                    ex,
                    ErrorCategory.Transcription
                );
                continue;
            }

            yield return (provider, selectionId);
        }
    }

    private void LogInvalidSelectionId(
        string providerRole,
        object provider,
        Exception exception,
        string errorCategory
    )
    {
        var message =
            $"Skipping {providerRole} '{provider.GetType().Name}' because its effective "
            + $"selection ID is invalid: {exception.Message}";
        Trace.WriteLine($"[PluginManager] {message}");
        _errorLog?.AddEntry(message, errorCategory);
    }

    // A misbehaving third-party plugin must not be able to abort the whole
    // capability rebuild: materialize each provider's additional roles inside a
    // try/catch so a throwing getter (or one that throws mid-enumeration) just
    // contributes nothing and is logged. Grouping/dedup downstream is unchanged.
    private static IEnumerable<ILlmProviderRole> SafeAdditionalLlmProviders(
        IAdditionalLlmProvidersProvider provider
    )
    {
        try
        {
            // Drop null entries: the downstream GroupBy key selector calls an extension
            // method (GetLlmSelectionId) that dereferences the instance, so a null from a
            // misbehaving plugin would throw outside this try/catch and abort the rebuild.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- AdditionalLlmProviders comes from a third-party plugin whose nullable annotation may lie
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- defensive: a misbehaving plugin may yield null elements despite the non-null annotation
            return provider.AdditionalLlmProviders?.Where(p => p is not null).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Skipping additional LLM providers from {provider.GetType().Name}: {ex.Message}"
            );
            return [];
        }
    }

    private static IEnumerable<ITranscriptionEngineRole> SafeAdditionalTranscriptionEngines(
        IAdditionalTranscriptionEnginesProvider provider
    )
    {
        try
        {
            // Drop null entries: the downstream GroupBy key selector calls an extension
            // method (GetTranscriptionSelectionId) that dereferences the instance, so a
            // null from a misbehaving plugin would throw outside this try/catch.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- AdditionalTranscriptionEngines comes from a third-party plugin whose nullable annotation may lie
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- defensive: a misbehaving plugin may yield null elements despite the non-null annotation
            return provider.AdditionalTranscriptionEngines?.Where(e => e is not null).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Skipping additional transcription engines from {provider.GetType().Name}: {ex.Message}"
            );
            return [];
        }
    }

    private void PersistEnabledState(string pluginId, bool enabled)
    {
        try
        {
            var current = _settings.Current;
            var updatedState = new Dictionary<string, bool>(current.PluginEnabledState) { [pluginId] = enabled };

            _settings.Save(current with { PluginEnabledState = updatedState });
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Failed to persist enabled state for {pluginId}: {ex.Message}"
            );
        }
    }

    /// <summary>
    ///     One-shot migration for users upgrading from a build where Groq/OpenAI
    ///     keys were stored at the top level of AppSettings. New installs leave
    ///     those fields empty so this is effectively a no-op.
    /// </summary>
    private async Task MigrateApiKeysAsync()
    {
        var settings = _settings.Current;
        var migratedGroq = false;
        var migratedOpenAi = false;

        if (!string.IsNullOrEmpty(settings.GroqApiKey))
        {
            migratedGroq = await MigrateKeyToPluginAsync(
                "com.typewhisper.groq",
                "api-key",
                settings.GroqApiKey
            );
        }

        if (!string.IsNullOrEmpty(settings.OpenAiApiKey))
        {
            migratedOpenAi = await MigrateKeyToPluginAsync(
                "com.typewhisper.openai",
                "api-key",
                settings.OpenAiApiKey
            );
        }

        if (migratedGroq || migratedOpenAi)
        {
            var current = _settings.Current;
            _settings.Save(
                current with
                {
                    GroqApiKey = migratedGroq ? "" : current.GroqApiKey,
                    OpenAiApiKey = migratedOpenAi ? "" : current.OpenAiApiKey,
                }
            );
        }
    }

    private async Task<bool> MigrateKeyToPluginAsync(
        string pluginId,
        string secretKey,
        string encryptedValue
    )
    {
        PluginHostServices? hostServices;
        lock (_lock)
        {
            _hostServices.TryGetValue(pluginId, out hostServices);
        }

        if (hostServices is null)
        {
            return false;
        }

        try
        {
            var decrypted = ApiKeyProtection.Decrypt(encryptedValue);
            if (string.IsNullOrEmpty(decrypted))
            {
                return false;
            }

            await hostServices.StoreSecretAsync(secretKey, decrypted);
            Trace.WriteLine($"[PluginManager] Migrated API key to plugin: {pluginId}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginManager] Failed to migrate API key for {pluginId}: {ex.Message}"
            );
            _errorLog?.AddEntry(
                $"Failed to migrate API key for plugin '{pluginId}': {ex.Message}",
                ErrorCategory.Plugin
            );
            return false;
        }
    }
}
