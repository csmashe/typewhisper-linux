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
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private readonly ISettingsService _settings;
    private readonly SystemCommandAvailabilityService? _commands;
    private string? _activeModelId;
    private TranscriptionAccelerationPreference? _activeModelAccelerationPreference;
    private Timer? _autoUnloadTimer;
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
    ///     Test seam for the CUDA-runtime preflight. The default delegate gates the
    ///     preload behind <see cref="SystemCommandAvailabilityService.HasCudaGpu" />
    ///     when a commands service was injected, so a system with CUDA runtime
    ///     libraries but no NVIDIA GPU does not silently load whisper.cpp with
    ///     <c>UseGpu = true</c>. Returns (success, message).
    /// </summary>
    internal Func<(bool Success, string Message)> CudaRuntimePreflight { get; set; }

    private (bool, string) DefaultCudaRuntimePreflight()
    {
        if (_commands is { HasCudaGpu: false })
        {
            return (false, "No NVIDIA GPU/driver detected.");
        }

        var ok = SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibraries(out var message);
        return (ok, message);
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
            if (_activeModelId is not null && IsPluginModel(_activeModelId))
            {
                var (pluginId, _) = ParsePluginModelId(_activeModelId);
                var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
                    e.PluginId == pluginId
                );
                if (plugin is not null)
                {
                    return new PluginTranscriptionEngineAdapter(plugin);
                }
            }

            return NoOpTranscriptionEngine.Instance;
        }
    }

    public ITranscriptionEnginePlugin? ActiveTranscriptionPlugin
    {
        get
        {
            if (_activeModelId is null || !IsPluginModel(_activeModelId))
            {
                return null;
            }

            var (pluginId, _) = ParsePluginModelId(_activeModelId);
            return PluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAutoUnload();
        // _modelLock is intentionally NOT disposed here. An outstanding
        // TranscriptionLease or a fire-and-forget UnloadModelAsync call
        // may call Release() after Dispose returns. SemaphoreSlim only
        // requires disposal when its AvailableWaitHandle has been
        // accessed (it has not), so leaving it live avoids a
        // double-release crash at zero cost.
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
        if (secondColon < 0)
        {
            throw new ArgumentException($"Invalid plugin model ID format: {modelId}");
        }

        return (modelId[(firstColon + 1)..secondColon], modelId[(secondColon + 1)..]);
    }

    public static string GetPluginModelId(string pluginId, string modelId)
    {
        return $"plugin:{pluginId}:{modelId}";
    }

    internal static TranscriptionAccelerationPreference GetAccelerationPreference(string? value)
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
    ///     Linux Auto resolver: tries the CUDA 12 preflight and resolves Auto →
    ///     NvidiaCuda on success, → Cpu on failure. Explicit preferences pass through
    ///     unchanged. Test seam optionally overrides the preflight.
    /// </summary>
    internal static TranscriptionAccelerationPreference ResolveAutoPreference(
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
            e.PluginId == pluginId
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
            e.PluginId == pluginId
        );

        if (plugin is null)
        {
            return false;
        }

        if (plugin.SupportsModelDownload)
        {
            return plugin.IsModelDownloaded(pluginModelId);
        }

        return plugin.IsConfigured;
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
            _modelLock.Release();
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
            _modelLock.Release();
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

    public void ScheduleAutoUnload()
    {
        CancelAutoUnload();

        var seconds = _settings.Current.ModelAutoUnloadSeconds;
        if (seconds <= 0 || ActiveModelId is null)
        {
            return;
        }

        _autoUnloadTimer = new Timer(seconds * 1000.0) { AutoReset = false };
        _autoUnloadTimer.Elapsed += (_, _) =>
        {
            Debug.WriteLine($"Auto-unloading model after {seconds}s idle");
            UnloadModel();
        };
        _autoUnloadTimer.Start();
    }

    public bool CanDeleteModel(string modelId)
    {
        if (!IsPluginModel(modelId))
        {
            return false;
        }

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = PluginManager.TranscriptionEngines.FirstOrDefault(e =>
            e.PluginId == pluginId
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

    public void DeleteModel(string modelId)
    {
        _ = DeleteModelAsync(modelId);
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
            _modelLock.Release();
        }
    }

    /// <summary>
    ///     Acquires exclusive use of the transcription engine: ensures the requested
    ///     model is loaded under <c>_modelLock</c> and returns a lease pinning the
    ///     active plugin. While the lease is held no other acquire — and no model
    ///     load, download, unload, or delete — can run, so the plugin's native model
    ///     cannot be swapped underneath the holder. The lease MUST be disposed.
    /// </summary>
    public async Task<TranscriptionLease> AcquireTranscriptionAsync(
        string? modelId = null,
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

            return new TranscriptionLease(_modelLock, plugin);
        }
        catch
        {
            _modelLock.Release();
            throw;
        }
    }

    /// <summary>
    ///     Like <see cref="AcquireTranscriptionAsync" />, but never loads a model and
    ///     returns <c>null</c> immediately when the model lock is already held, no
    ///     model resolves, or the requested model is not the one currently loaded.
    ///     Used for best-effort callers (partial transcripts) which must never
    ///     initiate a heavyweight load/download from the recording loop.
    /// </summary>
    public async Task<TranscriptionLease?> TryAcquireTranscriptionAsync(
        string? modelId = null,
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
                string.IsNullOrWhiteSpace(targetModelId)
                || ActiveModelId != targetModelId
                || ActiveTranscriptionPlugin is not { } plugin
            )
            {
                _modelLock.Release();
                return null;
            }

            CancelAutoUnload();
            return new TranscriptionLease(_modelLock, plugin);
        }
        catch
        {
            _modelLock.Release();
            throw;
        }
    }

    /// <summary>
    ///     One-time migration from bare model IDs (stored by older builds before
    ///     the plugin model ID scheme was introduced) to the "plugin:pluginId:modelId"
    ///     format that all current code expects. Safe to call repeatedly — no-ops
    ///     when the stored ID is already in the new format.
    /// </summary>
    public void MigrateSettings()
    {
        var current = _settings.Current;
        var changed = false;

        var migratedModelId = MigrateModelId(current.SelectedModelId);
        if (migratedModelId != current.SelectedModelId)
        {
            current = current with { SelectedModelId = migratedModelId };
            changed = true;
        }

        if (changed)
        {
            _settings.Save(current);
        }
    }

    public static string? MigrateModelId(string? modelId)
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
            _ => modelId
        };
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
            PluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        try
        {
            if (plugin.SupportsModelDownload && !plugin.IsModelDownloaded(pluginModelId))
            {
                SetStatus(modelId, ModelStatus.DownloadingModel(0));

                var progress = new Progress<double>(p =>
                    SetStatus(modelId, ModelStatus.DownloadingModel(p))
                );
                await plugin.DownloadModelAsync(pluginModelId, progress, cancellationToken);
            }

            await LoadModelCoreAsync(modelId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
            PluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (!plugin.IsConfigured && !plugin.SupportsModelDownload)
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

            // For CPU-only plugins (SherpaOnnx), skip the preflight + hard-error path
            // entirely. Their SetAccelerationPreference already handles a CUDA
            // preference by warning and falling back to CPU, so running the preflight
            // here would just throw on a CUDA-less host and block a perfectly valid
            // CPU-only model load. Still resolve Auto → Cpu locally so plugins never
            // see the unresolved Auto sentinel (SDK contract).
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
                var (ok, message) = CudaRuntimePreflight();
                if (!ok)
                {
                    // Explicit NvidiaCuda preserves the hard-error path so the user
                    // knows when CUDA is broken; Auto already resolved to Cpu above
                    // if CUDA wasn't visible, so this only fires for the explicit
                    // case (and for Auto on systems where the preflight returns
                    // success on the first call but fails on the second — pathological,
                    // surfaces the error rather than silently misloading).
                    throw new InvalidOperationException(message);
                }
            }

            plugin.SetAccelerationPreference(resolvedPreference);

            if (plugin.SupportsModelDownload)
            {
                await plugin.LoadModelAsync(pluginModelId, cancellationToken);
            }

            plugin.SelectModel(pluginModelId);
            SetStatus(modelId, ModelStatus.Ready);
            ActiveModelId = modelId;
            _activeModelAccelerationPreference = requestedPreference;
        }
        catch (Exception ex)
        {
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

        // Await the native teardown before releasing _modelLock so a queued
        // load/acquire/delete cannot enter the exclusive section while the
        // plugin's native model is still being disposed.
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

        // Unload succeeded. The model is no longer loaded but, for
        // download-capable plugins, still on disk — so drop the tracked
        // override and let GetStatus recompute real availability rather than
        // pinning NotDownloaded on a model that is merely unloaded.
        _modelStatuses.Remove(modelId);
        OnPropertyChanged(nameof(GetStatus));
        ActiveModelId = null;
        _activeModelAccelerationPreference = null;
    }

    private void CancelAutoUnload()
    {
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
                e.PluginId == pluginId
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

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    ///     Exclusive lease over the transcription engine. While a lease is held no
    ///     other acquire — and no model load, download, unload, or delete — can run,
    ///     so the plugin's native model cannot be swapped underneath the holder.
    ///     Disposing releases the model lock; a double dispose releases it only once.
    /// </summary>
    public sealed class TranscriptionLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _modelLock;
        private int _released;

        internal TranscriptionLease(SemaphoreSlim modelLock, ITranscriptionEnginePlugin plugin)
        {
            _modelLock = modelLock;
            Plugin = plugin;
        }

        /// <summary>The plugin pinned for the lifetime of this lease.</summary>
        public ITranscriptionEnginePlugin Plugin { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
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
    private readonly ITranscriptionEnginePlugin _plugin;

    public PluginTranscriptionEngineAdapter(ITranscriptionEnginePlugin plugin)
    {
        _plugin = plugin;
    }

    public bool IsModelLoaded => _plugin.IsConfigured && _plugin.SelectedModelId is not null;

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
        var result = await _plugin.TranscribeAsync(
            wavBytes,
            language,
            translate,
            null,
            cancellationToken
        );
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            NoSpeechProbability = result.NoSpeechProbability
        };
    }
}