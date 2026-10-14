using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>
///     Presents a local engine to the host while its model loading and inference run in a worker
///     process. Metadata, downloads, removal and settings stay with the in-process engine, which
///     never loads a model. A worker that crashed or hung is discarded and the request fails; the
///     next request starts a new worker, which loads the selected model again.
/// </summary>
internal sealed class IsolatedTranscriptionEngine
    : ITranscriptionEngineRole,
        ITranscriptionEngineSelectionIdentity,
        ITranscriptionLanguageSelectionCapabilities,
        IAsyncDisposable
{
    private readonly ITranscriptionEngineRole _inner;
    private readonly Func<TranscriptionAccelerationPreference, string?, CancellationToken, Task<ITranscriptionWorkerConnection>> _start;
    private readonly Action<PluginLogLevel, string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITranscriptionWorkerConnection? _worker;
    private TranscriptionWorkerState? _state;

    // Requests take this token before queueing; a stop cancels it and replaces it once it holds the gate.
    // Replaced sources are not disposed: requests may still link to their tokens.
    private readonly Lock _stopSync = new();
    private CancellationTokenSource _stop = new();
    private bool _disposed;

    // Engines keep an explicit backend in memory only, so each new worker is told again.
    private string? _computeBackend;
    private string? _workerComputeBackend;

    private IsolatedTranscriptionEngine(
        ITranscriptionEngineRole inner,
        Func<TranscriptionAccelerationPreference, string?, CancellationToken, Task<ITranscriptionWorkerConnection>> start,
        Action<PluginLogLevel, string> log
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
    }

    internal static IsolatedTranscriptionEngine Create(
        ITranscriptionEngineRole inner,
        IPluginProcessSupervisor processes,
        TranscriptionWorkerLaunch launch,
        Action<PluginLogLevel, string> log
    )
    {
        return new IsolatedTranscriptionEngine(
            inner,
            async (acceleration, computeBackend, ct) => await TranscriptionWorkerProcess
                .StartAsync(processes, launch, acceleration, computeBackend, log, ct)
                .ConfigureAwait(false),
            log
        );
    }

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

    // What the worker actually runs on, once one has started.
    public TranscriptionAccelerationStatus AccelerationStatus => _state?.Status ?? _inner.AccelerationStatus;

    public void SelectModel(string modelId)
    {
        _inner.SelectModel(modelId);
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        // The next request starts a worker with the new preference, which supersedes an earlier
        // explicit backend just as it does in the engine itself.
        _inner.SetAccelerationPreference(preference);
        _computeBackend = null;
    }

    public async Task ConfigureComputeBackendAsync(string backend)
    {
        await StopWorkerThenAsync(async () =>
        {
            await _inner.ConfigureComputeBackendAsync(backend).ConfigureAwait(false);
            _computeBackend = backend;
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

    /// <summary>Loads the model in the worker, starting one if none runs.</summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var response = await RunAsync(
            new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load, ModelId = modelId },
            default,
            ct
        ).ConfigureAwait(false);
        // Some engines select the model they load. Mirror that on the in-process engine, whose
        // selection drives metadata such as translation support and the next request's model.
        if (response.State?.SelectedModelId == modelId && _inner.SelectedModelId != modelId)
        {
            _inner.SelectModel(modelId);
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
        CancellationToken ct
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

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TranscriptionWorkerMessage response;
            try
            {
                var worker = await EnsureWorkerAsync(token).ConfigureAwait(false);
                // File, API and recovery requests may arrive without a prior load, and a new worker
                // starts cold. Loading gets its own budget before the audio deadline starts.
                TranscriptionWorkerMessage? load = null;
                if (
                    request is { Command: TranscriptionWorkerCommands.Transcribe, ModelId: { } modelId }
                    && worker.State?.LoadedModelId != modelId
                )
                {
                    load = await worker
                        .SendAsync(
                            new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load, ModelId = modelId },
                            default,
                            token
                        )
                        .ConfigureAwait(false);
                }

                response = load?.Error is not null
                    ? load
                    : await worker.SendAsync(request, payload, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TranscriptionWorkerCrashedException or TranscriptionWorkerRequestTimeoutException)
            {
                // The worker's last output is the only record of a native crash.
                if (ex is TranscriptionWorkerCrashedException { Diagnostics: { Length: > 0 } diagnostics })
                {
                    _log(PluginLogLevel.Error, $"{ex.Message}{Environment.NewLine}{diagnostics}");
                }

                // Nothing the worker did can be trusted after this; the next request starts fresh.
                await DiscardWorkerAsync().ConfigureAwait(false);
                throw;
            }

            if (response.State is { } state)
            {
                _state = state;
            }

            return response.Error is { } error ? throw TranscriptionWorkerProtocol.ToException(error) : response;
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw StoppedException(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Ended by a stop, not by the caller, who must not take this for its own cancellation.
    private InvalidOperationException StoppedException(OperationCanceledException ex)
    {
        return new InvalidOperationException(ProviderDisplayName + " was stopped before it finished. Try again.", ex);
    }

    private async Task<ITranscriptionWorkerConnection> EnsureWorkerAsync(CancellationToken ct)
    {
        var preference = _inner.AccelerationPreference;
        if (
            _worker is { IsAlive: true } running
            && running.Acceleration == preference
            && _workerComputeBackend == _computeBackend
        )
        {
            return running;
        }

        await DiscardWorkerAsync().ConfigureAwait(false);
        // Snapshot: a preference change during startup must still be seen as a change afterwards.
        var computeBackend = _computeBackend;
        _worker = await _start(preference, computeBackend, ct).ConfigureAwait(false);
        _workerComputeBackend = computeBackend;
        if (_worker.State is { } state)
        {
            _state = state;
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
