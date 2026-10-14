using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>The worker kept crashing; nothing it was doing completed.</summary>
internal sealed class TranscriptionWorkerFaultedException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
///     Presents a local engine to the host while its model loading and inference run in a worker
///     process. Metadata, downloads, removal and settings stay with the in-process engine, which
///     never loads a model. A crashed worker is replaced and the request retried; repeated crashes
///     on an accelerated backend switch the engine to the CPU, and further crashes pause it briefly
///     instead of looping. A worker that hung is not retried: it already cost the user its timeout.
/// </summary>
internal sealed class IsolatedTranscriptionEngine
    : ITranscriptionEngineRole,
        ITranscriptionEngineSelectionIdentity,
        ITranscriptionLanguageSelectionCapabilities,
        IAsyncDisposable
{
    internal const int MaximumAttempts = 3;
    internal const int CrashesBeforePause = 3;
    private const int CrashesBeforeCpuFallback = 2;

    internal static TimeSpan PauseAfterRepeatedCrashes { get; } = TimeSpan.FromSeconds(30);

    private readonly ITranscriptionEngineRole _inner;
    private readonly Func<TranscriptionAccelerationPreference, string?, CancellationToken, Task<ITranscriptionWorkerConnection>> _start;
    private readonly Action<PluginLogLevel, string> _log;
    private readonly Action _statusChanged;
    private readonly Func<string> _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITranscriptionWorkerConnection? _worker;
    private TranscriptionWorkerState? _state;

    // The acceleration the reported state was produced with; another choice makes it stale.
    private TranscriptionAccelerationPreference _stateAcceleration;

    // Requests take this token before queueing; a stop cancels it and replaces it once it holds the gate.
    // Replaced sources are not disposed: requests may still link to their tokens.
    private readonly Lock _stopSync = new();
    private CancellationTokenSource _stop = new();
    private bool _disposed;

    // Engines keep an explicit backend in memory only, so each new worker is told again.
    private string? _computeBackend;
    private string? _workerComputeBackend;

    // The saved settings the running worker read at its start.
    private string? _workerSettings;

    // Crash bookkeeping, changed only under the gate.
    private int _consecutiveCrashes;
    private bool _cpuFallback;
    private TranscriptionAccelerationPreference _fallbackFrom;
    private DateTimeOffset _pausedUntil;

    internal IsolatedTranscriptionEngine(
        ITranscriptionEngineRole inner,
        Func<TranscriptionAccelerationPreference, string?, CancellationToken, Task<ITranscriptionWorkerConnection>> start,
        Action<PluginLogLevel, string> log,
        Action? statusChanged = null,
        TimeProvider? time = null,
        Func<string>? settings = null
    )
    {
        // Streaming sessions are callback objects that cannot cross the process boundary.
        if (inner.SupportsStreaming)
        {
            throw new ArgumentException("Streaming engines cannot run in a transcription worker.", nameof(inner));
        }

        _inner = inner;
        _start = start;
        _log = log;
        _statusChanged = statusChanged ?? (() => { });
        _settings = settings ?? (() => "");
        _time = time ?? TimeProvider.System;
    }

    // statusChanged is called off the gate when crashes changed how the engine runs.
    internal static IsolatedTranscriptionEngine Create(
        ITranscriptionEngineRole inner,
        IPluginProcessSupervisor processes,
        TranscriptionWorkerLaunch launch,
        Action<PluginLogLevel, string> log,
        Action? statusChanged = null
    )
    {
        return new IsolatedTranscriptionEngine(
            inner,
            async (acceleration, computeBackend, ct) => await TranscriptionWorkerProcess
                .StartAsync(processes, launch, acceleration, computeBackend, log, ct)
                .ConfigureAwait(false),
            log,
            statusChanged
        );
    }

    /// <summary>Whether crashes switched this engine to the CPU until its acceleration choice changes.</summary>
    internal bool UsesCpuFallback => FallbackActive();

    /// <summary>The process id of the running worker, or null when none runs.</summary>
    internal int? WorkerProcessId => _worker is { IsAlive: true } worker ? worker.ProcessId : null;

    public string PluginId => _inner.PluginId;
    public string TranscriptionSelectionId => _inner.GetTranscriptionSelectionId();
    public string ProviderId => _inner.ProviderId;
    public string ProviderDisplayName => _inner.ProviderDisplayName;
    public bool IsConfigured => _inner.IsConfigured;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => _inner.TranscriptionModels;
    public string? SelectedModelId => _inner.SelectedModelId;
    public bool SupportsTranslation => _inner.SupportsTranslation;
    public bool SupportsModelDownload => _inner.SupportsModelDownload;
    public DictionaryTermsBudget? DictionaryTermsBudget => _inner.DictionaryTermsBudget;
    public bool SupportsStructuredDictionaryTerms => _inner.SupportsStructuredDictionaryTerms;
    public bool SupportsLanguageHints => _inner.SupportsLanguageHints;
    public IReadOnlyList<string> SupportedLanguages => _inner.SupportedLanguages;
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends => _inner.SupportedAccelerationBackends;
    public bool ProvisionsCudaRuntimeOnDemand => _inner.ProvisionsCudaRuntimeOnDemand;
    public bool IsCudaRuntimeProvisioned => _inner.IsCudaRuntimeProvisioned;
    public TranscriptionAccelerationPreference AccelerationPreference => _inner.AccelerationPreference;

    // Language support is metadata of the selected model, so the in-process engine answers.
    public LanguageSelectionSupport AutomaticDetectionSupport =>
        _inner is ITranscriptionLanguageSelectionCapabilities capabilities
            ? capabilities.AutomaticDetectionSupport
            : LanguageSelectionSupport.Unknown;

    public LanguageSelectionSupport ExplicitSelectionSupport =>
        _inner is ITranscriptionLanguageSelectionCapabilities capabilities
            ? capabilities.ExplicitSelectionSupport
            : LanguageSelectionSupport.Unknown;

    // What the worker actually runs on, once one has started. A worker never needs an app restart:
    // a changed choice or an explicit load starts a new one.
    public TranscriptionAccelerationStatus AccelerationStatus =>
        FallbackActive()
            ? new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Using CPU",
                "The engine stopped unexpectedly on the graphics card, so TypeWhisper switched it to the CPU. "
                + "Change the acceleration setting to try the graphics card again."
            )
            : _state?.Status is { } status && _stateAcceleration == EffectivePreference()
                ? status with { RequiresRestart = false }
                : _inner.AccelerationStatus;

    public void SelectModel(string modelId)
    {
        SelectInnerModel(modelId);
    }

    // The engine may save its selection as a setting. Each request names its model, so that alone
    // does not make the running worker's settings stale.
    private void SelectInnerModel(string modelId)
    {
        var current = _workerSettings == _settings();
        _inner.SelectModel(modelId);
        if (current)
        {
            _workerSettings = _settings();
        }
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        // The next request starts a worker with the new preference, which supersedes an earlier
        // explicit backend just as it does in the engine itself.
        _inner.SetAccelerationPreference(preference);
        _computeBackend = null;
        if (preference != _fallbackFrom)
        {
            _cpuFallback = false;
        }
    }

    public async Task ConfigureComputeBackendAsync(string backend)
    {
        await StopWorkerThenAsync(async () =>
        {
            await _inner.ConfigureComputeBackendAsync(backend).ConfigureAwait(false);
            _computeBackend = backend;
            // A new explicit choice, which the CPU fallback must not override.
            _cpuFallback = false;
        }).ConfigureAwait(false);
    }

    public Task EnsureCudaRuntimeReadyAsync(IProgress<double>? progress, CancellationToken ct)
    {
        return _inner.EnsureCudaRuntimeReadyAsync(progress, ct);
    }

    public async Task ClearCudaRuntimeAsync(CancellationToken ct)
    {
        // The worker may have the runtime's libraries loaded.
        await StopWorkerThenAsync(() => _inner.ClearCudaRuntimeAsync(ct)).ConfigureAwait(false);
    }

    public bool IsModelDownloaded(string modelId)
    {
        return _inner.IsModelDownloaded(modelId);
    }

    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        return _inner.DownloadModelAsync(modelId, progress, ct);
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        // Only the loaded model's files may be open in the worker; deleting another one waits for
        // running work instead of ending it, and still holds the gate so no load can race it.
        if (_state?.LoadedModelId == modelId)
        {
            await StopWorkerThenAsync(() => _inner.DeleteModelAsync(modelId, ct)).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The request that held the gate may have loaded this very model meanwhile.
            if (_state?.LoadedModelId == modelId)
            {
                await DiscardWorkerAsync().ConfigureAwait(false);
                _state = null;
            }

            await _inner.DeleteModelAsync(modelId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the model in the worker, starting one if none runs. An explicit load also ends a crash pause.</summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var response = await RunAsync(
            new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load, ModelId = modelId },
            default,
            ct,
            explicitLoad: true
        ).ConfigureAwait(false);
        // Some engines select the model they load. Mirror that on the in-process engine, whose
        // selection drives metadata such as translation support and the next request's model.
        if (response.State?.SelectedModelId == modelId && _inner.SelectedModelId != modelId)
        {
            SelectInnerModel(modelId);
        }
    }

    // Progress callbacks cannot cross the process boundary.
    public Task LoadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        return LoadModelAsync(modelId, ct);
    }

    /// <summary>Ends the worker, which releases the model and all native memory.</summary>
    public Task UnloadModelAsync()
    {
        return StopWorkerAsync();
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        return TranscribeCoreAsync(
            new TranscriptionWorkerMessage { Language = language, Translate = translate, Prompt = prompt },
            wavAudio,
            ct
        );
    }

    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        return TranscribeCoreAsync(
            new TranscriptionWorkerMessage
            {
                LanguageHints = [.. languageHints], Translate = translate, Prompt = prompt,
            },
            wavAudio,
            ct
        );
    }

    // Progress callbacks cannot cross the process boundary; the hints still reach the engine whole.
    public Task<PluginTranscriptionResult> TranscribeStreamingWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct
    )
    {
        return TranscribeWithLanguageHintsAsync(wavAudio, languageHints, translate, prompt, ct);
    }

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        TranscriptionWorkerMessage request,
        byte[] audio,
        CancellationToken ct
    )
    {
        var response = await RunAsync(
            request with { Command = TranscriptionWorkerCommands.Transcribe, ModelId = _inner.SelectedModelId },
            audio,
            ct
        ).ConfigureAwait(false);
        return TranscriptionWorkerProtocol.FromResult(
            response.Result ?? throw new InvalidDataException("The transcription worker returned no result.")
        );
    }

    private async Task<TranscriptionWorkerMessage> RunAsync(
        TranscriptionWorkerMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct,
        bool explicitLoad = false
    )
    {
        CancellationToken stop;
        lock (_stopSync)
        {
            stop = _stop.Token;
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        var token = running.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw StoppedException(ex);
        }

        var statusChanged = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (explicitLoad)
            {
                _pausedUntil = default;
                _consecutiveCrashes = 0;
                // A worker that loaded on the CPU after its GPU runtime failed is pinned there; an
                // explicit load is the restart its status asked for.
                if (_state?.Status.RequiresRestart == true)
                {
                    await DiscardWorkerAsync().ConfigureAwait(false);
                }
            }

            if (_time.GetUtcNow() < _pausedUntil)
            {
                throw new TranscriptionWorkerFaultedException(PausedMessage());
            }

            var restartedForRuntime = false;
            for (var attempt = 1; ; attempt++)
            {
                var acceleration = EffectivePreference();
                TranscriptionWorkerMessage response;
                try
                {
                    response = await SendAsync(request, payload, token).ConfigureAwait(false);
                }
                catch (TranscriptionWorkerCrashedException crash)
                {
                    await DiscardWorkerAsync().ConfigureAwait(false);
                    statusChanged |= RecordCrash(crash, acceleration);
                    if (_time.GetUtcNow() < _pausedUntil)
                    {
                        throw new TranscriptionWorkerFaultedException(PausedMessage(), crash);
                    }

                    // A hang already cost the user its timeout; running it again would double that.
                    if (crash is TranscriptionWorkerUnresponsiveException)
                    {
                        throw;
                    }

                    if (attempt >= MaximumAttempts)
                    {
                        throw new TranscriptionWorkerFaultedException(
                            ProviderDisplayName + " stopped unexpectedly again. Try again in a moment or choose another model.",
                            crash
                        );
                    }

                    continue;
                }
                catch (TranscriptionWorkerRequestTimeoutException)
                {
                    await DiscardWorkerAsync().ConfigureAwait(false);
                    throw;
                }

                if (response.State is { } state)
                {
                    SetState(state, acceleration);
                }

                if (response.Error is { } error)
                {
                    // An engine whose native runtime can only load once per process (whisper.cpp
                    // after a failed GPU load) asks for a restart; a new worker is one, so it gets one try.
                    if (restartedForRuntime || response.State?.Status.RequiresRestart != true || error.Kind == "canceled")
                    {
                        throw TranscriptionWorkerProtocol.ToException(error);
                    }

                    restartedForRuntime = true;
                    await DiscardWorkerAsync().ConfigureAwait(false);
                    attempt--;
                    continue;
                }

                _consecutiveCrashes = 0;
                return response;
            }
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw StoppedException(ex);
        }
        finally
        {
            _gate.Release();
            if (statusChanged)
            {
                _statusChanged();
            }
        }
    }

    // One attempt on the current worker, starting one if needed. Callers hold the gate.
    private async Task<TranscriptionWorkerMessage> SendAsync(
        TranscriptionWorkerMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken token
    )
    {
        var worker = await EnsureWorkerAsync(token).ConfigureAwait(false);
        // File, API and recovery requests may arrive without a prior load, and a new worker
        // starts cold. Loading gets its own budget before the audio deadline starts.
        // ReSharper disable once InvertIf -- inverting would repeat the request send in both branches.
        if (
            request is { Command: TranscriptionWorkerCommands.Transcribe, ModelId: { } modelId }
            && worker.State?.LoadedModelId != modelId
        )
        {
            var load = await worker
                .SendAsync(
                    new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load, ModelId = modelId },
                    default,
                    token
                )
                .ConfigureAwait(false);
            if (load.Error is not null)
            {
                return load;
            }
        }

        return await worker.SendAsync(request, payload, token).ConfigureAwait(false);
    }

    // Returns whether the engine changed how it runs, which the host should show.
    private bool RecordCrash(TranscriptionWorkerCrashedException crash, TranscriptionAccelerationPreference acceleration)
    {
        _consecutiveCrashes++;
        // The worker's last output is the only record of a native crash.
        _log(
            PluginLogLevel.Error,
            $"{ProviderDisplayName} worker crashed ({_consecutiveCrashes} in a row, acceleration {acceleration}): {crash.Message}"
            + (crash.Diagnostics.Length > 0 ? Environment.NewLine + crash.Diagnostics : "")
        );
        if (_consecutiveCrashes >= CrashesBeforeCpuFallback && !FallbackActive() && CanFallBackToCpu())
        {
            _cpuFallback = true;
            _fallbackFrom = _inner.AccelerationPreference;
            _consecutiveCrashes = 0;
            _log(
                PluginLogLevel.Warning,
                $"{ProviderDisplayName} switched to the CPU after repeated crashes with {_fallbackFrom} acceleration."
            );
            return true;
        }

        if (_consecutiveCrashes < CrashesBeforePause)
        {
            return false;
        }

        _pausedUntil = _time.GetUtcNow() + PauseAfterRepeatedCrashes;
        _consecutiveCrashes = 0;
        _log(PluginLogLevel.Warning, PausedMessage());
        return true;
    }

    private string PausedMessage()
    {
        return ProviderDisplayName + " stopped unexpectedly several times, so TypeWhisper paused it briefly. "
               + $"Try again in {PauseAfterRepeatedCrashes.TotalSeconds:0} seconds, load the model again, or choose another model.";
    }

    private bool CanFallBackToCpu()
    {
        return EffectivePreference() != TranscriptionAccelerationPreference.Cpu
               && _state?.Status.ActiveBackend is not TranscriptionAccelerationBackend.Cpu
               && _inner.SupportedAccelerationBackends.Contains(TranscriptionAccelerationBackend.Cpu)
               && _inner.SupportedAccelerationBackends.Any(backend => backend != TranscriptionAccelerationBackend.Cpu);
    }

    // The fallback holds until the user picks an acceleration again, which is a new explicit choice.
    private bool FallbackActive()
    {
        if (_cpuFallback && _inner.AccelerationPreference != _fallbackFrom)
        {
            _cpuFallback = false;
        }

        return _cpuFallback;
    }

    private TranscriptionAccelerationPreference EffectivePreference()
    {
        return FallbackActive() ? TranscriptionAccelerationPreference.Cpu : _inner.AccelerationPreference;
    }

    private void SetState(TranscriptionWorkerState state, TranscriptionAccelerationPreference acceleration)
    {
        _state = state;
        _stateAcceleration = acceleration;
    }

    // Ended by a stop, not by the caller, who must not take this for its own cancellation.
    private InvalidOperationException StoppedException(OperationCanceledException ex)
    {
        return new InvalidOperationException(ProviderDisplayName + " was stopped before it finished. Try again.", ex);
    }

    private async Task<ITranscriptionWorkerConnection> EnsureWorkerAsync(CancellationToken ct)
    {
        var preference = EffectivePreference();
        // The fallback replaces an explicit GPU backend as well.
        var computeBackend = FallbackActive() ? null : _computeBackend;
        // A worker reads the plugin's settings once, so a changed setting needs a new one.
        var settings = _settings();
        if (
            _worker is { IsAlive: true } running
            && running.Acceleration == preference
            && _workerComputeBackend == computeBackend
            && _workerSettings == settings
        )
        {
            return running;
        }

        await DiscardWorkerAsync().ConfigureAwait(false);
        // Snapshots: a change during startup must still be seen as a change afterwards.
        _worker = await _start(preference, computeBackend, ct).ConfigureAwait(false);
        _workerComputeBackend = computeBackend;
        _workerSettings = settings;
        if (_worker.State is { } state)
        {
            SetState(state, preference);
        }

        return _worker;
    }

    private async Task DiscardWorkerAsync()
    {
        var worker = _worker;
        _worker = null;
        if (worker is not null)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task StopWorkerAsync()
    {
        return StopWorkerThenAsync(() => Task.CompletedTask);
    }

    // The follow-up runs inside the gate, so no request can start a worker that misses it.
    private async Task StopWorkerThenAsync(Func<Task> then)
    {
        await EnterGateToStopAsync().ConfigureAwait(false);
        try
        {
            await DiscardWorkerAsync().ConfigureAwait(false);
            _state = null;
            await then().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // A stop cancels the running and queued requests rather than waiting behind a hung worker, which
    // gets its cancellation grace and is then ended.
    private async Task EnterGateToStopAsync()
    {
        CancellationTokenSource stopped;
        lock (_stopSync)
        {
            stopped = _stop;
        }

        await stopped.CancelAsync().ConfigureAwait(false);
        // Never canceled: a stop must finish.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        // Requests arriving until now took the canceled token and stop too; only once the stop
        // holds the gate can new ones queue behind it.
        lock (_stopSync)
        {
            if (_stop == stopped)
            {
                _stop = new CancellationTokenSource();
            }
        }
    }

    /// <summary>Ends the worker. The in-process engine belongs to its plugin and is not disposed here.</summary>
    public async ValueTask DisposeAsync()
    {
        await EnterGateToStopAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DiscardWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
