using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Timer = System.Timers.Timer;

namespace TypeWhisper.Linux.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly SystemCommandAvailabilityService? _commands;
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ModelStatus> _modelStatuses = new();
    private readonly ISettingsService _settings;
    private TranscriptionAccelerationPreference? _activeModelAccelerationPreference;
    private string? _activeModelId;
    // Guards _autoUnloadTimer, _autoUnloadGeneration and _disposed. Load/unload/acquire paths
    // already hold _modelLock, but Dispose() runs on an arbitrary thread and must not block on
    // that async lock — without its own gate it can race a lease's re-arm and leave a zombie timer.
    private readonly Lock _timerGate = new();
    private Timer? _autoUnloadTimer;
    private int _autoUnloadGeneration;
    private bool _disposed;

    public ModelManagerService(
        PluginManager pluginManager,
        ISettingsService settings,
        SystemCommandAvailabilityService? commands = null)
    {
        PluginManager = pluginManager;
        _settings = settings;
        _commands = commands;
        CudaRuntimePreflight = DefaultCudaRuntimePreflight;
    }

    /// <summary>
    ///     Test seam for the CUDA-runtime preflight. When a commands service was injected the
    ///     default delegate gates preload behind <see cref="SystemCommandAvailabilityService.HasCudaGpu" />
    ///     so a CUDA-capable host without an NVIDIA GPU doesn't silently load with <c>UseGpu = true</c>.
    ///     Returns (success, message).
    /// </summary>
    internal Func<(bool Success, string Message)> CudaRuntimePreflight { get; set; }

    /// <summary>Test seam: true while the idle auto-unload timer is armed and pending.</summary>
    internal bool IsAutoUnloadArmed
    {
        get
        {
            lock (_timerGate)
            {
                return _autoUnloadTimer is { Enabled: true };
            }
        }
    }

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set
        {
            _activeModelId = value;
            OnPropertyChanged();
        }
    }

    public PluginManager PluginManager { get; }

    public ITranscriptionEngine Engine
    {
        get
        {
            if (_activeModelId is null || !IsPluginModel(_activeModelId))
            {
                return NoOpTranscriptionEngine.Instance;
            }

            var (pluginId, _) = ParsePluginModelId(_activeModelId);
            var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
                e.GetTranscriptionSelectionId() == pluginId
            );
            if (plugin is not null)
            {
                return new PluginTranscriptionEngineAdapter(plugin);
            }

            return NoOpTranscriptionEngine.Instance;
        }
    }

    public ITranscriptionEngineRole? ActiveTranscriptionPlugin => GetTranscriptionPlugin(_activeModelId);

    /// <summary>
    ///     Resolves the transcription plugin that owns <paramref name="modelId" /> (a
    ///     <c>plugin:&lt;id&gt;:&lt;model&gt;</c> identifier), or null when the id is not a
    ///     plugin model or no matching engine is loaded. Lets callers target the engine for
    ///     a specific (e.g. UI-selected) model rather than only the active one.
    /// </summary>
    public ITranscriptionEngineRole? GetTranscriptionPlugin(string? modelId)
    {
        if (modelId is null || !IsPluginModel(modelId))
        {
            return null;
        }

        var (pluginId, _) = ParsePluginModelId(modelId);
        return PluginManager.TranscriptionEngines.FirstOrDefault(e => e.GetTranscriptionSelectionId() == pluginId);
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelAutoUnloadLocked();
        }

        // _modelLock is intentionally NOT disposed: an outstanding TranscriptionLease or
        // fire-and-forget UnloadModelAsync may Release() after Dispose returns. SemaphoreSlim
        // only requires disposal when AvailableWaitHandle has been accessed (it has not).
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static bool IsPluginModel(string modelId)
    {
        return modelId.StartsWith("plugin:");
    }

    public static (string PluginId, string ModelId) ParsePluginModelId(string modelId)
    {
        if (!IsPluginModel(modelId))
        {
            throw new ArgumentException($"Not a plugin model ID: {modelId}");
        }

        var firstColon = modelId.IndexOf(':');
        var secondColon = modelId.IndexOf(':', firstColon + 1);
        return secondColon < 0
            ? throw new ArgumentException($"Invalid plugin model ID format: {modelId}")
            : (modelId[(firstColon + 1)..secondColon], modelId[(secondColon + 1)..]);
    }

    public static string GetPluginModelId(string pluginId, string modelId)
    {
        return $"plugin:{pluginId}:{modelId}";
    }

    public ModelStatus GetStatus(string modelId)
    {
        if (_modelStatuses.TryGetValue(modelId, out var tracked))
        {
            return tracked;
        }

        if (!IsPluginModel(modelId))
        {
            return ModelStatus.NotDownloaded;
        }

        if (_activeModelId == modelId)
        {
            return ModelStatus.Ready;
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
            e.GetTranscriptionSelectionId() == pluginId
        );

        if (plugin is null)
        {
            return ModelStatus.NotDownloaded;
        }

        if (plugin.SupportsModelDownload)
        {
            return plugin.IsModelDownloaded(pluginModelId)
                ? ModelStatus.Ready
                : ModelStatus.NotDownloaded;
        }

        return plugin.IsConfigured ? ModelStatus.Ready : ModelStatus.NotDownloaded;
    }

    public bool IsDownloaded(string modelId)
    {
        if (!IsPluginModel(modelId))
        {
            return false;
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
            e.GetTranscriptionSelectionId() == pluginId
        );

        if (plugin is null)
        {
            return false;
        }

        return plugin.SupportsModelDownload
            ? plugin.IsModelDownloaded(pluginModelId)
            : plugin.IsConfigured;
    }

    public async Task DownloadAndLoadModelAsync(
        string modelId,
        CancellationToken cancellationToken = default
    )
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            await DownloadAndLoadModelCoreAsync(modelId, cancellationToken);
        }
        finally
        {
            try
            {
                ScheduleAutoUnload();
            }
            finally
            {
                _modelLock.Release();
            }
        }
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            await LoadModelCoreAsync(modelId, cancellationToken);
        }
        finally
        {
            try
            {
                ScheduleAutoUnload();
            }
            finally
            {
                _modelLock.Release();
            }
        }
    }

    /// <summary>
    ///     Fire-and-forget unload. Mirrors <see cref="DeleteModel" />: callers on
    ///     non-blockable threads (the auto-unload timer, app shutdown) use this so
    ///     they never block waiting on <c>_modelLock</c>.
    /// </summary>
    public void UnloadModel()
    {
        _ = UnloadModelAsync();
    }

    public async Task UnloadModelAsync()
    {
        await _modelLock.WaitAsync();
        try
        {
            await UnloadModelCoreAsync();
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private void ScheduleAutoUnload()
    {
        var seconds = _settings.Current.ModelAutoUnloadSeconds;
        var armable = seconds > 0 && ActiveModelId is not null;

        lock (_timerGate)
        {
            CancelAutoUnloadLocked();

            // Never arm after disposal: a lease outstanding when Dispose() runs re-arms here on
            // its DisposeAsync, which would otherwise leave a zombie timer that fires plugin
            // unloading during or after app teardown.
            if (_disposed || !armable)
            {
                return;
            }

            // Stop()/Dispose() cannot recall an Elapsed callback already dispatched to the
            // thread pool, so a superseded timer can still fire after a newer model was loaded.
            // Each callback carries the generation it was armed with; see
            // UnloadIfGenerationCurrentAsync for where that is validated.
            var generation = _autoUnloadGeneration;

            // System.Timers.Timer throws for intervals above int.MaxValue ms, and
            // ModelAutoUnloadSeconds is a raw setting a corrupt or hand-edited config could push
            // past that. Every load/lease path runs through here, so a throw must never be possible.
            var intervalMs = Math.Min(seconds * 1000.0, int.MaxValue);
            _autoUnloadTimer = new Timer(intervalMs) { AutoReset = false };
            _autoUnloadTimer.Elapsed += (_, _) =>
                _ = UnloadIfGenerationCurrentAsync(generation, seconds);
            _autoUnloadTimer.Start();
        }
    }

    public bool CanDeleteModel(string modelId)
    {
        if (!IsPluginModel(modelId))
        {
            return false;
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
            e.GetTranscriptionSelectionId() == pluginId
        );

        return plugin is { SupportsModelDownload: true } && plugin.IsModelDownloaded(pluginModelId);
    }

    public async Task DeleteModelAsync(
        string modelId,
        CancellationToken cancellationToken = default
    )
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            await DeleteModelCoreAsync(modelId, cancellationToken);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    // ReSharper disable once UnusedMember.Global  public API surface (fire-and-forget wrapper over DeleteModelAsync); not currently called in-tree
    public void DeleteModel(string modelId)
    {
        _ = DeleteModelAsync(modelId);
    }

    /// <summary>
    ///     Deletes every on-demand-provisioning engine's cached CUDA runtime (the shared
    ///     CUDA math libraries plus each engine's own GPU build) so a corrupt cache can
    ///     be recovered: the next process start re-provisions from scratch. The active
    ///     model is unloaded first so no <c>.so</c> is in use. Every engine is attempted
    ///     (one failing doesn't stop the others), but if any clear fails the collected
    ///     failures are thrown as an aggregate — so a corrupt runtime is never reported to
    ///     the user as repaired when it is still on disk. Note: libraries already loaded
    ///     this session are held until exit, so a restart is required for the fresh
    ///     re-download to take effect.
    /// </summary>
    public async Task ClearCudaRuntimeCacheAsync(CancellationToken cancellationToken = default)
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            await UnloadModelCoreAsync();

            var failures = new List<string>();
            foreach (
                var plugin in PluginManager.TranscriptionEngines.Where(e =>
                    e.ProvisionsCudaRuntimeOnDemand
                )
            )
            {
                try
                {
                    await plugin.ClearCudaRuntimeAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The caller asked to stop: propagate immediately instead of
                    // continuing to delete other engines' caches and folding the cancel
                    // into the aggregate failure below.
                    throw;
                }
                catch (Exception ex)
                {
                    // Attempt every engine, but record the failure so it can be surfaced:
                    // a swallowed delete failure would tell the user the corrupt runtime
                    // was cleared when it is still on disk.
                    Debug.WriteLine(
                        $"ClearCudaRuntimeAsync failed for {plugin.ProviderId}: {ex.Message}"
                    );
                    failures.Add($"{plugin.ProviderId}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "Failed to clear the GPU runtime cache: " + string.Join("; ", failures)
                );
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public async Task<bool> EnsureModelLoadedAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default
    )
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            return await EnsureModelLoadedCoreAsync(modelId, cancellationToken);
        }
        finally
        {
            try
            {
                ScheduleAutoUnload();
            }
            finally
            {
                _modelLock.Release();
            }
        }
    }

    /// <summary>
    ///     Ensures the requested model is loaded under <c>_modelLock</c> and returns a lease
    ///     pinning the active plugin. While held, no other acquire or model load/unload/delete
    ///     can run. The lease MUST be disposed to release the lock.
    /// </summary>
    public async Task<TranscriptionLease> AcquireTranscriptionAsync(
        string? modelId = null,
        bool keepModelWarm = false,
        CancellationToken cancellationToken = default
    )
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            if (!await EnsureModelLoadedCoreAsync(modelId, cancellationToken))
            {
                throw new InvalidOperationException("No transcription model loaded.");
            }

            var plugin =
                ActiveTranscriptionPlugin
                ?? throw new InvalidOperationException("No transcription engine loaded.");

            return new TranscriptionLease(_modelLock, plugin, this, keepModelWarm);
        }
        catch
        {
            try
            {
                ScheduleAutoUnload();
            }
            finally
            {
                _modelLock.Release();
            }

            throw;
        }
    }

    /// <summary>
    ///     Like <see cref="AcquireTranscriptionAsync" /> but never loads a model; returns
    ///     <c>null</c> immediately if the lock is busy, nothing resolves, or the requested
    ///     model isn't already loaded. Used by best-effort callers (partial transcripts)
    ///     that must never trigger a heavyweight load from the recording loop.
    /// </summary>
    public async Task<TranscriptionLease?> TryAcquireTranscriptionAsync(
        string? modelId = null,
        bool keepModelWarm = false,
        CancellationToken cancellationToken = default
    )
    {
        if (!await _modelLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            var targetModelId = modelId ?? _settings.Current.SelectedModelId;
            if (
                !string.IsNullOrWhiteSpace(targetModelId)
                && ActiveModelId == targetModelId
                && ActiveTranscriptionPlugin is { } plugin
            )
            {
                CancelAutoUnload();
                return new TranscriptionLease(_modelLock, plugin, this, keepModelWarm);
            }
        }
        catch
        {
            try
            {
                ScheduleAutoUnload();
            }
            finally
            {
                _modelLock.Release();
            }

            throw;
        }

        try
        {
            ScheduleAutoUnload();
        }
        finally
        {
            _modelLock.Release();
        }

        return null;
    }

    /// <summary>
    ///     Migrates bare model IDs (pre-plugin-scheme builds) to "plugin:pluginId:modelId" format.
    ///     Idempotent — no-ops when the stored ID is already in the new format.
    /// </summary>
    public void MigrateSettings()
    {
        _settings.Update(current =>
            current with
            {
                SelectedModelId = MigrateModelId(current.SelectedModelId),
                FileTranscriptionModelOverride = MigrateOverrideModelId(
                    current.FileTranscriptionModelOverride
                ),
                WatchFolderModelOverride = MigrateOverrideModelId(
                    current.WatchFolderModelOverride
                ),
            }
        );
    }

    private static string? MigrateModelId(string? modelId)
    {
        return modelId switch
        {
            "parakeet-tdt-0.6b" => GetPluginModelId(
                "com.typewhisper.sherpa-onnx",
                "parakeet-tdt-0.6b"
            ),
            "canary-1b-flash" or "canary-180m-flash" => GetPluginModelId(
                "com.typewhisper.sherpa-onnx",
                "canary-180m-flash"
            ),
            "plugin:com.typewhisper.voxtral:mistral-whisper" =>
                GetPluginModelId("com.typewhisper.voxtral", "voxtral-mini-latest"),
            _ => modelId,
        };
    }

    /// <summary>
    ///     Unlike the global selection, overrides are (engine, model) pairs, so only
    ///     fully-qualified legacy IDs are rewritten: a bare "mistral-whisper" may belong
    ///     to a different engine (e.g. an OpenAI-compatible custom model).
    /// </summary>
    private static string? MigrateOverrideModelId(string? modelId)
    {
        return modelId switch
        {
            "plugin:com.typewhisper.voxtral:mistral-whisper" =>
                GetPluginModelId("com.typewhisper.voxtral", "voxtral-mini-latest"),
            _ => modelId,
        };
    }

    private static TranscriptionAccelerationPreference GetAccelerationPreference(string? value)
    {
        var normalized = AppSettings.NormalizeLocalModelAcceleration(value);
        return normalized switch
        {
            AppSettings.LocalModelAccelerationNvidiaCuda =>
                TranscriptionAccelerationPreference.NvidiaCuda,
            AppSettings.LocalModelAccelerationCpu => TranscriptionAccelerationPreference.Cpu,
            _ => TranscriptionAccelerationPreference.Auto,
        };
    }

    /// <summary>
    ///     Resolves Auto → NvidiaCuda or Cpu via the CUDA 12 preflight; explicit preferences
    ///     pass through unchanged. <paramref name="cudaPreflight" /> overrides the default for tests.
    /// </summary>
    private static TranscriptionAccelerationPreference ResolveAutoPreference(
        TranscriptionAccelerationPreference requested,
        Func<bool>? cudaPreflight = null
    )
    {
        if (requested != TranscriptionAccelerationPreference.Auto)
        {
            return requested;
        }

        var preflight = cudaPreflight
                        ?? (() => SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibraries(out _));

        return preflight()
            ? TranscriptionAccelerationPreference.NvidiaCuda
            : TranscriptionAccelerationPreference.Cpu;
    }

    private (bool, string) DefaultCudaRuntimePreflight()
    {
        if (_commands is { HasCudaGpu: false })
        {
            return (false, "No NVIDIA GPU/driver detected.");
        }

        var ok = SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibraries(out var message);
        return (ok, message);
    }

    private async Task DownloadAndLoadModelCoreAsync(
        string modelId,
        CancellationToken cancellationToken
    )
    {
        if (!IsPluginModel(modelId))
        {
            throw new ArgumentException($"Unknown model: {modelId}");
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin =
            PluginManager.TranscriptionEngines.FirstOrDefault(e => e.GetTranscriptionSelectionId() == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        try
        {
            if (plugin.SupportsModelDownload && !plugin.IsModelDownloaded(pluginModelId))
            {
                SetStatus(modelId, ModelStatus.DownloadingModel(0));

                // Progress<T> posts callbacks asynchronously to the captured
                // SynchronizationContext, so a late download report can run AFTER the load
                // below sets the terminal Ready status and clobber it with a stale
                // DownloadingModel(1.0) — leaving the UI pinned at 100% and never flipping to
                // "Ready". Gate the handler so it no-ops once the download has returned
                // (mirrors the load-progress gate in LoadModelCoreAsync).
                var downloadInProgress = true;
                var progress = new Progress<double>(p =>
                {
                    // ReSharper disable once AccessToModifiedClosure -- intentional gate: downloadInProgress is flipped under Volatile in the enclosing finally so a late Progress callback no-ops (see comment above).
                    if (!Volatile.Read(ref downloadInProgress))
                        return;
                    SetStatus(modelId, ModelStatus.DownloadingModel(p));
                });
                try
                {
                    await plugin.DownloadModelAsync(pluginModelId, progress, cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref downloadInProgress, false);
                }

                // A cancellation-ignoring plugin may have finished writing its artifact;
                // that work is uninterruptible here, so checkpoint before the service
                // transitions to loading and leave the artifact in place for GetStatus.
                cancellationToken.ThrowIfCancellationRequested();
            }

            await LoadModelCoreAsync(modelId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-requested cancellation: drop this operation's transient entry so
            // GetStatus re-derives from the artifact state.
            ClearStatus(modelId);
            throw;
        }
        catch (Exception ex)
        {
            // Includes an OperationCanceledException whose caller token is NOT requested:
            // per the SDK cancellation-origin contract that is a dependency fault (e.g. a
            // stalled third-party download surfacing as a bare TaskCanceledException) and
            // must record Failed, never clear to an implied Ready.
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    private async Task LoadModelCoreAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!IsPluginModel(modelId))
        {
            throw new ArgumentException($"Unknown model: {modelId}");
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin =
            PluginManager.TranscriptionEngines.FirstOrDefault(e => e.GetTranscriptionSelectionId() == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (plugin is { IsConfigured: false, SupportsModelDownload: false })
        {
            throw new InvalidOperationException(
                $"{plugin.ProviderDisplayName}: not configured (missing API key or model)."
            );
        }

        CancelAutoUnload();
        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            var requestedPreference = GetAccelerationPreference(
                _settings.Current.LocalModelAcceleration
            );
            var pluginSupportsCuda = plugin.SupportedAccelerationBackends.Contains(
                TranscriptionAccelerationBackend.NvidiaCuda
            );

            // CPU-only plugins (those that don't list NvidiaCuda) handle a CUDA
            // preference internally by falling back to CPU, so the preflight hard-error
            // path is irrelevant and would break valid CPU loads on CUDA-less hosts.
            // Always resolve Auto → Cpu locally so plugins never receive the unresolved
            // Auto sentinel (SDK contract).
            TranscriptionAccelerationPreference resolvedPreference;
            if (pluginSupportsCuda)
            {
                resolvedPreference = ResolveAutoPreference(
                    requestedPreference,
                    () => CudaRuntimePreflight().Success
                );
            }
            else
            {
                resolvedPreference =
                    requestedPreference == TranscriptionAccelerationPreference.Auto
                        ? TranscriptionAccelerationPreference.Cpu
                        : requestedPreference;
            }

            if (
                pluginSupportsCuda
                && resolvedPreference == TranscriptionAccelerationPreference.NvidiaCuda
            )
            {
                if (plugin.ProvisionsCudaRuntimeOnDemand)
                {
                    // This plugin downloads + preloads its own CUDA runtime during the
                    // load and falls back to CPU itself (reporting via AccelerationStatus)
                    // if that fails, so a missing *system* CUDA install is not fatal —
                    // skipping the host preflight is what lets on-demand provisioning run
                    // on a driver-only host. Still hard-fail when there's no NVIDIA GPU at
                    // all, so we don't kick off a multi-hundred-MB runtime download with
                    // nothing to run it on. (_commands is null only in unit tests, where
                    // the absence of a known-missing GPU lets the provisioning path run.)
                    if (_commands is { HasCudaGpu: false })
                    {
                        throw new InvalidOperationException("No NVIDIA GPU/driver detected.");
                    }
                }
                else
                {
                    // Plugins that rely on a host-provided CUDA runtime keep the hard-error
                    // path so broken CUDA is visible. Auto would have resolved to Cpu above;
                    // this branch is for explicit requests (or the pathological
                    // double-preflight-inconsistency case).
                    var (ok, message) = CudaRuntimePreflight();
                    if (!ok)
                    {
                        throw new InvalidOperationException(message);
                    }
                }
            }

            plugin.SetAccelerationPreference(resolvedPreference);

            if (plugin.SupportsModelDownload)
            {
                // A self-provisioning engine may download a multi-hundred-MB GPU runtime
                // during the load (first CUDA use). Surface that as DownloadingModel so the
                // UI shows a real progress bar instead of the static LoadingModel spinner
                // set above; when provisioning finishes (or none is needed) the plugin
                // reports 1.0 and we drop back to LoadingModel for the native init.
                //
                // Progress<T> posts callbacks asynchronously to the captured
                // SynchronizationContext, so a late report could otherwise run AFTER this
                // load returns and clobber the terminal Ready status (set below) with a
                // stale Loading/Downloading one. Gate the handler so it no-ops once the
                // load has returned.
                var loadInProgress = true;
                var loadProgress = new Progress<double>(p =>
                {
                    // ReSharper disable once AccessToModifiedClosure -- intentional gate: loadInProgress is flipped under Volatile in the enclosing finally so a late Progress callback no-ops (see comment above).
                    if (!Volatile.Read(ref loadInProgress))
                        return;
                    SetStatus(
                        modelId,
                        p >= 1.0 ? ModelStatus.LoadingModel : ModelStatus.DownloadingModel(p)
                    );
                });
                try
                {
                    await plugin.LoadModelAsync(pluginModelId, loadProgress, cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref loadInProgress, false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            plugin.SelectModel(pluginModelId);
            SetStatus(modelId, ModelStatus.Ready);
            ActiveModelId = modelId;
            _activeModelAccelerationPreference = requestedPreference;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearStatus(modelId);
            throw;
        }
        catch (Exception ex)
        {
            // Unrequested OperationCanceledException lands here deliberately — a
            // dependency fault must surface as Failed (cancellation-origin contract).
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    private async Task UnloadModelCoreAsync()
    {
        CancelAutoUnload();
        if (ActiveModelId is not { } modelId)
        {
            return;
        }

        // Await native teardown before releasing _modelLock so no queued
        // load/acquire/delete can enter while the plugin's model is still disposing.
        var plugin = ActiveTranscriptionPlugin;
        if (plugin is not null)
        {
            try
            {
                await plugin.UnloadModelAsync();
            }
            catch (Exception ex)
            {
                // Teardown failed: the native model may still be loaded. Leave
                // ActiveModelId and the tracked status untouched so we neither
                // misreport availability nor lose the active model a retry
                // would target.
                Debug.WriteLine($"UnloadModelAsync failed: {ex.Message}");
                return;
            }
        }

        // Unload succeeded: model is gone from memory but still on disk for download-capable
        // plugins, so drop the tracked status and let GetStatus recompute real availability.
        ClearStatus(modelId);
        ActiveModelId = null;
        _activeModelAccelerationPreference = null;
    }

    /// <summary>
    ///     Idle-timer unload. Checking the generation before taking <c>_modelLock</c> would not be
    ///     enough: a load or lease can win the lock in that gap, re-arm, and release — leaving this
    ///     already-validated callback to unload a model that was just loaded. Every re-arm happens
    ///     under <c>_modelLock</c>, so validating after acquiring it serializes check and unload.
    /// </summary>
    private async Task UnloadIfGenerationCurrentAsync(int generation, int idleSeconds)
    {
        await _modelLock.WaitAsync();
        try
        {
            lock (_timerGate)
            {
                if (_disposed || generation != _autoUnloadGeneration)
                {
                    return;
                }
            }

            Debug.WriteLine($"Auto-unloading model after {idleSeconds}s idle");
            await UnloadModelCoreAsync();
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private void CancelAutoUnload()
    {
        lock (_timerGate)
        {
            CancelAutoUnloadLocked();
        }
    }

    private void CancelAutoUnloadLocked()
    {
        // Bump first: this retires any Elapsed callback already in flight from the timer
        // being torn down, whether or not a replacement is armed afterwards.
        _autoUnloadGeneration++;
        _autoUnloadTimer?.Stop();
        _autoUnloadTimer?.Dispose();
        _autoUnloadTimer = null;
    }

    private async Task DeleteModelCoreAsync(string modelId, CancellationToken cancellationToken)
    {
        if (ActiveModelId == modelId)
        {
            var plugin = ActiveTranscriptionPlugin;
            if (plugin is not null)
            {
                await plugin.UnloadModelAsync();
            }

            ActiveModelId = null;
            _activeModelAccelerationPreference = null;
        }

        if (IsPluginModel(modelId))
        {
            var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
            var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
                e.GetTranscriptionSelectionId() == pluginId
            );

            if (plugin is { SupportsModelDownload: true })
            {
                await plugin.DeleteModelAsync(pluginModelId, cancellationToken);
            }
        }

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    private async Task<bool> EnsureModelLoadedCoreAsync(
        string? modelId,
        CancellationToken cancellationToken
    )
    {
        var targetModelId = modelId ?? _settings.Current.SelectedModelId;
        if (string.IsNullOrWhiteSpace(targetModelId))
        {
            return false;
        }

        var targetPreference = GetAccelerationPreference(
            _settings.Current.LocalModelAcceleration
        );

        if (
            ActiveModelId == targetModelId
            && _activeModelAccelerationPreference == targetPreference
        )
        {
            CancelAutoUnload();
            return true;
        }

        if (!IsDownloaded(targetModelId))
        {
            await DownloadAndLoadModelCoreAsync(targetModelId, cancellationToken);
        }
        else
        {
            await LoadModelCoreAsync(targetModelId, cancellationToken);
        }

        return true;
    }

    private void SetStatus(string modelId, ModelStatus status)
    {
        _modelStatuses[modelId] = status;
        OnPropertyChanged(nameof(GetStatus));
    }

    private void ClearStatus(string modelId)
    {
        if (_modelStatuses.TryRemove(modelId, out _))
        {
            OnPropertyChanged(nameof(GetStatus));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    ///     Exclusive lease over the transcription engine. While held, no other acquire or
    ///     model load/unload/delete can run. Dispose releases the lock (idempotent).
    /// </summary>
    public sealed class TranscriptionLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _modelLock;
        private readonly ModelManagerService _owner;
        private readonly bool _keepModelWarm;
        private int _released;

        internal TranscriptionLease(
            SemaphoreSlim modelLock,
            ITranscriptionEngineRole plugin,
            ModelManagerService owner,
            bool keepModelWarm
        )
        {
            _modelLock = modelLock;
            Plugin = plugin;
            _owner = owner;
            _keepModelWarm = keepModelWarm;
        }

        /// <summary>The plugin pinned for the lifetime of this lease.</summary>
        public ITranscriptionEngineRole Plugin { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            // Re-arm (unless the caller asked to keep the model warm) BEFORE releasing
            // _modelLock: the lock is still held here, so this is the last point at which
            // touching _autoUnloadTimer is guaranteed serialized against every other
            // load/unload/acquire path. Never let a caller thread touch the timer after
            // the lock is released. Release in finally so a scheduling failure can never
            // strand the lock and deadlock every subsequent load/unload/acquire.
            try
            {
                if (!_keepModelWarm)
                {
                    _owner.ScheduleAutoUnload();
                }
            }
            finally
            {
                _modelLock.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class NoOpTranscriptionEngine : ITranscriptionEngine
{
    public static readonly NoOpTranscriptionEngine Instance = new();

    public bool IsModelLoaded => false;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void UnloadModel() { }

    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(new TranscriptionResult { Text = string.Empty });
    }
}

internal sealed class PluginTranscriptionEngineAdapter : ITranscriptionEngine
{
    private readonly ITranscriptionEngineRole _plugin;

    public PluginTranscriptionEngineAdapter(ITranscriptionEngineRole plugin)
    {
        _plugin = plugin;
    }

    public bool IsModelLoaded => _plugin is { IsConfigured: true, SelectedModelId: not null };

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void UnloadModel() { }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default
    )
    {
        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate;
        var languageSelection = LanguageSelectionResolver.Resolve(language);
        var result = await _plugin.TranscribeAsync(
            wavBytes,
            languageSelection,
            translate,
            null,
            cancellationToken
        );
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            NoSpeechProbability = result.NoSpeechProbability,
        };
    }
}
