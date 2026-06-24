using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.Plugin.WhisperCpp;

public sealed class WhisperCppPlugin
    : ITypeWhisperPlugin,
        ITranscriptionEnginePlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string NoSpeechThresholdKey = "noSpeechThreshold";
    private const float DefaultNoSpeechThreshold = 0.6f;
    private static readonly IReadOnlyList<ModelDefinition> Models =
    [
        new(
            "tiny",
            "Tiny",
            GgmlType.Tiny,
            QuantizationType.NoQuantization,
            "ggml-tiny.bin",
            "~75 MB",
            75,
            99,
            false
        ),
        new(
            "tiny.en",
            "Tiny (English)",
            GgmlType.TinyEn,
            QuantizationType.NoQuantization,
            "ggml-tiny.en.bin",
            "~75 MB",
            75,
            1,
            false
        ),
        new(
            "tiny-q5_0",
            "Tiny (Q5_0)",
            GgmlType.Tiny,
            QuantizationType.Q5_0,
            "ggml-tiny-q5_0.bin",
            "~31 MB",
            31,
            99,
            false
        ),
        new(
            "base",
            "Base",
            GgmlType.Base,
            QuantizationType.NoQuantization,
            "ggml-base.bin",
            "~142 MB",
            142,
            99,
            true
        ),
        new(
            "base.en",
            "Base (English)",
            GgmlType.BaseEn,
            QuantizationType.NoQuantization,
            "ggml-base.en.bin",
            "~142 MB",
            142,
            1,
            false
        ),
        new(
            "base-q5_0",
            "Base (Q5_0)",
            GgmlType.Base,
            QuantizationType.Q5_0,
            "ggml-base-q5_0.bin",
            "~57 MB",
            57,
            99,
            true
        ),
        new(
            "small",
            "Small",
            GgmlType.Small,
            QuantizationType.NoQuantization,
            "ggml-small.bin",
            "~466 MB",
            466,
            99,
            false
        ),
        new(
            "small.en",
            "Small (English)",
            GgmlType.SmallEn,
            QuantizationType.NoQuantization,
            "ggml-small.en.bin",
            "~466 MB",
            466,
            1,
            false
        ),
        new(
            "small-q5_0",
            "Small (Q5_0)",
            GgmlType.Small,
            QuantizationType.Q5_0,
            "ggml-small-q5_0.bin",
            "~182 MB",
            182,
            99,
            false
        ),
        new(
            "medium",
            "Medium",
            GgmlType.Medium,
            QuantizationType.NoQuantization,
            "ggml-medium.bin",
            "~1.5 GB",
            1530,
            99,
            false
        ),
        new(
            "medium.en",
            "Medium (English)",
            GgmlType.MediumEn,
            QuantizationType.NoQuantization,
            "ggml-medium.en.bin",
            "~1.5 GB",
            1530,
            1,
            false
        ),
        new(
            "medium-q5_0",
            "Medium (Q5_0)",
            GgmlType.Medium,
            QuantizationType.Q5_0,
            "ggml-medium-q5_0.bin",
            "~601 MB",
            601,
            99,
            false
        ),
        new(
            "large-v3-turbo",
            "Large V3 Turbo",
            GgmlType.LargeV3Turbo,
            QuantizationType.NoQuantization,
            "ggml-large-v3-turbo.bin",
            "~1.6 GB",
            1620,
            99,
            false
        ),
        new(
            "large-v3-turbo-q5_0",
            "Large V3 Turbo (Q5_0)",
            GgmlType.LargeV3Turbo,
            QuantizationType.Q5_0,
            "ggml-large-v3-turbo-q5_0.bin",
            "~684 MB",
            684,
            99,
            false
        ),
        // Full large-v3 (32-layer decoder, not turbo's distilled 4-layer) garbles
        // far less on short cue words; fits a 1070's 8 GB VRAM under NVIDIA CUDA.
        new(
            "large-v3",
            "Large V3",
            GgmlType.LargeV3,
            QuantizationType.NoQuantization,
            "ggml-large-v3.bin",
            "~3.1 GB",
            3095,
            99,
            false
        ),
        new(
            "large-v3-q5_0",
            "Large V3 (Q5_0)",
            GgmlType.LargeV3,
            QuantizationType.Q5_0,
            "ggml-large-v3-q5_0.bin",
            "~1.1 GB",
            1081,
            99,
            false
        ),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Drives the on-demand CUDA runtime downloads (cudart/cuBLAS wheels + the ~167 MB
    // whisper CUDA nupkg). HttpClient.Timeout bounds the WHOLE request including the
    // streamed body — even with ResponseHeadersRead — so the default 100 s deadline
    // would cancel these multi-hundred-MB fetches mid-stream on any ordinary link.
    // Use a generous ceiling (matching GemmaLocal's large-model client) and rely on
    // the per-call CancellationToken for user-initiated cancellation. The
    // SocketsHttpHandler.ConnectTimeout bounds a socket that never establishes (the
    // 2 h total timeout doesn't catch that quickly); ResilientDownloader's per-read
    // idle watchdog bounds a half-open socket mid-body to seconds, not the 2 h ceiling.
    private readonly HttpClient _httpClient =
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromHours(2)
        };
    private IPluginHostServices? _host;
    private WhisperFactory? _factory;
    private CudaRuntimeProvisioner? _cudaProvisioner;
    private WhisperCudaRuntimeInstaller? _whisperCudaInstaller;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private string _computeBackend = "cpu";
    private bool _runtimeLibraryOrderInitialized;

    // Set when WhisperFactory.FromPath throws at the native LIBRARY-load layer.
    // Whisper.net caches that failure in a process-wide static Lazy, so once it
    // happens no later FromPath (on any backend) can succeed — the only recovery is
    // an app restart. We short-circuit subsequent loads instead of re-entering
    // FromPath and re-throwing Whisper.net's cached failure.
    private bool _nativeRuntimeLoadFailed;
    private TranscriptionAccelerationPreference _accelerationPreference =
        TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _accelerationStatus =
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU");
    private float _noSpeechThreshold = DefaultNoSpeechThreshold;

    public string PluginId => "com.typewhisper.whisper-cpp";
    public string PluginName => "whisper.cpp (Local)";
    public string PluginVersion => "1.0.0";

    public string ProviderId => "whisper-cpp";
    public string ProviderDisplayName => "Local (whisper.cpp)";
    public bool IsConfigured => true;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => true;
    public bool SupportsModelDownload => true;
    public IReadOnlyList<string> SupportedLanguages => [];

    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];

    // LoadModelAsync downloads + preloads the CUDA runtime on demand and falls back to
    // CPU itself, so the host need not require a system CUDA install for explicit CUDA.
    public bool ProvisionsCudaRuntimeOnDemand => true;

    // CUDA is ready only when BOTH cudart+cuBLAS (system-or-cached) AND whisper.cpp's
    // GPU native build are present; a partial state reports false so the host keeps
    // offering the download action. Pure file/cache inspection — no driver probe.
    public bool IsCudaRuntimeProvisioned =>
        _cudaProvisioner?.IsProfileSatisfied(CudaRuntimeProfile.WhisperCublas) == true
        && _whisperCudaInstaller?.IsInstalled == true;

    public TranscriptionAccelerationPreference AccelerationPreference => _accelerationPreference;

    public TranscriptionAccelerationStatus AccelerationStatus => _accelerationStatus;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        Models
            .Select(model => new PluginModelInfo(model.Id, model.DisplayName)
            {
                SizeDescription = model.SizeDescription,
                EstimatedSizeMB = model.EstimatedSizeMB,
                IsRecommended = model.IsRecommended,
                LanguageCount = model.LanguageCount,
            })
            .ToList();

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _selectedModelId = host.GetSetting<string>("selectedModel");
        _noSpeechThreshold = ReadNoSpeechThreshold(host);

        // Create the CUDA provisioner/installer eagerly so IsCudaRuntimeProvisioned can
        // report a warm cache immediately after a restart (the host gates CUDA selection
        // on it), not only after a download has been attempted. Both are cheap to build;
        // the ?? lets tests inject fakes before activate.
        _cudaProvisioner ??= new CudaRuntimeProvisioner(
            CudaRuntimeProvisioner.DefaultCacheRoot(),
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );
        _whisperCudaInstaller ??= new WhisperCudaRuntimeInstaller(
            host.PluginAssetDirectory,
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );

        host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    private static float ReadNoSpeechThreshold(IPluginHostServices host)
    {
        var raw = host.GetSetting<string>(NoSpeechThresholdKey);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultNoSpeechThreshold;

        if (
            float.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            && parsed is >= 0f and <= 1f
        )
            return parsed;

        return DefaultNoSpeechThreshold;
    }

    public async Task DeactivateAsync()
    {
        await UnloadModelAsync();
        _host = null;
    }

    public void SelectModel(string modelId)
    {
        _ = GetModel(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    // Hop to the thread pool so a UI-thread caller doesn't block on _gate.Wait()
    // inside the sync helper that backs SetAccelerationPreference.
    public Task ConfigureComputeBackendAsync(string backend) =>
        Task.Run(() => TryConfigureComputeBackend(backend));

    private bool TryConfigureComputeBackend(string backend)
    {
        var normalized = string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";

        // Hold the same gate used by load/transcribe paths so the backend
        // swap and the factory disposal don't race a concurrent operation.
        _gate.Wait();
        try
        {
            if (_computeBackend == normalized)
                return true;

            // RuntimeLibraryOrder is consulted once when the native library first
            // loads (see EnsureRuntimeLibraryOrderInitialized). Once that has run,
            // further backend swaps would desync the managed factory's UseGpu flag
            // from the actual loaded native runtime, so refuse the change.
            if (_runtimeLibraryOrderInitialized)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Cannot switch compute backend to '{normalized}' after the native runtime has loaded ({_computeBackend}). Restart the app to change backends."
                );
                return false;
            }

            _computeBackend = normalized;
            if (_factory is not null)
            {
                DisposeFactoryUnsafe();
                _loadedModelId = null;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        var backend = preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => "cuda",
            TranscriptionAccelerationPreference.Cpu => "cpu",
            _ => "cpu",
        };

        // Always record the host's last requested preference so the SDK getter
        // reflects user intent, even when the runtime can't honour it yet.
        _accelerationPreference = preference;

        _accelerationStatus = TryConfigureComputeBackend(backend)
            ? CreatePendingAccelerationStatus(preference)
            // Swap was rejected because the native runtime is already pinned.
            // Report the still-active backend with RequiresRestart=true so the UI
            // surfaces the mismatch instead of silently dropping the request.
            : CreateLoadedAccelerationStatus(_computeBackend, preference);
    }

    public bool IsModelDownloaded(string modelId) => File.Exists(GetModelPath(modelId));

    public async Task DownloadModelAsync(
        string modelId,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        await _gate.WaitAsync(ct);
        try
        {
            var model = GetModel(modelId);
            var modelPath = GetModelPath(modelId);
            var modelDirectory = Path.GetDirectoryName(modelPath)!;
            Directory.CreateDirectory(modelDirectory);

            if (File.Exists(modelPath))
            {
                progress?.Report(1.0);
                return;
            }

            var tempPath = Path.Combine(
                modelDirectory,
                $"{Path.GetFileName(modelPath)}.{Guid.NewGuid():N}.tmp"
            );

            try
            {
                await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    model.Type,
                    model.Quantization,
                    ct
                );

                var buffer = new byte[81920];
                long bytesCopied = 0;
                // The download stream isn't seekable, so its Length is unavailable;
                // fall back to the model's known size as the denominator so the
                // progress bar grows instead of jumping 0% → 100%. Report(1.0)
                // below snaps it to exactly 100% regardless of estimate drift.
                var totalBytes = modelStream.CanSeek
                    ? modelStream.Length
                    : model.EstimatedSizeMB * 1024L * 1024L;

                await using (
                    var fileStream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        true
                    )
                )
                {
                    while (true)
                    {
                        var read = await modelStream.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        bytesCopied += read;

                        if (totalBytes > 0)
                            progress?.Report(Math.Min(1.0, (double)bytesCopied / totalBytes));
                    }

                    await fileStream.FlushAsync(ct);
                }

                // Atomic on the same filesystem: a crash between delete and move
                // would otherwise leave no model in place. Move(overwrite: true)
                // is implemented via rename(2) on Linux, which is atomic.
                File.Move(tempPath, modelPath, overwrite: true);
                progress?.Report(1.0);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct) =>
        LoadModelAsync(modelId, null, ct);

    public async Task LoadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model files not found for: {modelId}", modelPath);

        // Snapshot the requested backend (and whether the native runtime is already
        // pinned) under the gate, then release it: provisioning the CUDA runtime can
        // download hundreds of MB on first use, and holding the load/transcribe gate
        // for that long would needlessly block a backend switch the user might make
        // mid-download.
        string desiredBackend;
        bool alreadyPinned;
        await _gate.WaitAsync(ct);
        try
        {
            desiredBackend = _computeBackend;
            alreadyPinned = _runtimeLibraryOrderInitialized;
        }
        finally
        {
            _gate.Release();
        }

        // Set if a CUDA attempt fails and we fall back to CPU, so the final status
        // can explain why rather than hard-failing an explicit CUDA request.
        string? cudaUnavailableDetail = null;
        var downgradedToCpu = false;

        // Provision + download the GPU runtime before touching the factory. Skipped
        // once the native runtime is pinned: it's already loaded, so re-fetching it
        // would be pointless (and a pinned mismatch is surfaced as restart-required).
        if (string.Equals(desiredBackend, "cuda", StringComparison.OrdinalIgnoreCase) && !alreadyPinned)
        {
            try
            {
                await EnsureCudaRuntimeReadyAsync(progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"whisper.cpp CUDA runtime unavailable ({ex.Message}); falling back to CPU."
                        + (_cudaProvisioner is { } provisioner
                            ? $" Shared CUDA runtime cache: {provisioner.CacheDirectory}."
                            : "")
                );
                cudaUnavailableDetail = ex.Message;
                desiredBackend = "cpu";
                downgradedToCpu = true;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            // A prior native library-load failure poisoned Whisper.net's static loader
            // for this process; re-entering FromPath would just re-throw its cached
            // failure. Fail fast with a restart-required message instead.
            if (_nativeRuntimeLoadFailed)
                throw new InvalidOperationException(
                    "The whisper.cpp native runtime failed to load earlier in this session "
                        + "and cannot be reloaded. Restart TypeWhisper to try again."
                );

            if (_runtimeLibraryOrderInitialized)
            {
                // The native runtime is pinned for the process; load on whatever it
                // was pinned to. _computeBackend can't drift from it (backend swaps
                // are refused after the pin), so the factory's UseGpu flag stays
                // consistent and any unsatisfiable request already reads as
                // restart-required in the acceleration status.
                cudaUnavailableDetail = null;
            }
            else
            {
                // Not pinned yet. A user-initiated backend switch during the (possibly
                // long) download leaves _computeBackend pointing somewhere else; abort
                // the now-stale load rather than pinning the process to a backend the
                // user no longer wants. A CPU downgrade we made ourselves is expected,
                // not a user switch, so it doesn't trip this.
                if (!downgradedToCpu
                    && !string.Equals(_computeBackend, desiredBackend, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Compute backend changed during model load; reload to apply the new backend."
                    );

                _computeBackend = desiredBackend;
            }

            // Commit the process-wide loader path only now that the backend choice has
            // survived the re-check. Setting it during provisioning (outside the gate)
            // would leave RuntimeOptions.LibraryPath pointing at the CUDA cache if the
            // user switched to CPU mid-download and we aborted the load above — a later
            // CPU load would then resolve the runtime against the wrong directory.
            // Harmless to (re)set when already pinned: LibraryPath is consulted only at
            // the one-time native load.
            if (string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase)
                && _whisperCudaInstaller is not null)
                RuntimeOptions.LibraryPath = _whisperCudaInstaller.LibraryPath;

            DisposeFactoryUnsafe();
            ApplyRuntimeLibraryOrderUnsafe();

            // Two distinct failure layers here, handled differently:
            //
            // 1. Native LIBRARY load (the .so set). Whisper.net does this exactly once
            //    per process via a static Lazy<LoadResult> and caches the outcome —
            //    success OR failure — so there is no in-process retry for it. The
            //    recoverable causes (CUDA libraries or the GPU backend missing /
            //    uninstallable) are handled earlier: EnsureCudaRuntimeReadyAsync throws
            //    and the caller downgrades desiredBackend to CPU, so the order applied
            //    above is [Cpu]. A library-load failure here surfaces as a throw.
            //
            // 2. Native CONTEXT creation (whisper_init for this model). This is
            //    per-factory, NOT cached, and — critically — FromPath does NOT throw
            //    when it fails: Whisper.net stores a null context and only throws later
            //    at CreateBuilder. We must validate now so we never publish a model as
            //    "loaded" that can't actually transcribe. And because the context is
            //    per-factory, we CAN recover from a GPU context failure by rebuilding
            //    it with UseGpu=false on the already-loaded native runtime (no second
            //    library load), which yields a working CPU transcriber.
            try
            {
                _factory = WhisperFactory.FromPath(modelPath, CreateFactoryOptions());
            }
            catch (Exception ex)
                when (string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase)
                    && RuntimeOptions.LoadedLibrary is null)
            {
                // The CUDA native library SET failed to load (a driver/runtime symbol
                // gap the cuInit probe didn't catch). Whisper.net caches this failure in
                // a process-wide static Lazy, so a CPU retry via FromPath here would read
                // the poisoned result and throw again — we can't recover the backend
                // without a restart. Record it (so the next load short-circuits), surface
                // restart-required, and fail cleanly instead of letting a raw exception
                // escape or pinning a broken runtime.
                //
                // RuntimeOptions.LoadedLibrary is set by Whisper.net only when its one-shot
                // loader successfully loaded a runtime; it stays null ONLY on a genuine
                // library-load failure. Gating on it ensures a per-model/file exception
                // (corrupt model, IO error) — where the library loaded fine — is NOT treated
                // as loader poisoning: it falls through to propagate (or, for a null GPU
                // context, to the TryValidateFactory CPU fallback below) and never blocks a
                // later CPU load.
                _nativeRuntimeLoadFailed = true;
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"whisper.cpp CUDA native runtime failed to load ({ex.Message}); "
                        + "restart required to use CPU."
                );
                _accelerationStatus = CreateCudaUnavailableStatus(
                    "The GPU runtime could not be loaded. Restart TypeWhisper to use CPU."
                );
                throw new InvalidOperationException(
                    $"Failed to load the whisper.cpp CUDA runtime for model '{modelId}'; "
                        + "restart TypeWhisper to use CPU.",
                    ex
                );
            }

            if (!TryValidateFactory(_factory)
                && string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase))
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    "whisper.cpp GPU context could not be created; falling back to CPU compute."
                );
                cudaUnavailableDetail ??= "The GPU context could not be created; using CPU.";
                DisposeFactoryUnsafe();
                _computeBackend = "cpu";
                // Same pinned native runtime; only the context is rebuilt (UseGpu=false).
                _factory = WhisperFactory.FromPath(modelPath, CreateFactoryOptions());
            }

            if (!TryValidateFactory(_factory))
            {
                DisposeFactoryUnsafe();
                throw new InvalidOperationException(
                    $"Failed to load whisper model '{modelId}': the model could not be initialized."
                );
            }

            // First successful factory creation loads + pins the native runtime; from
            // here on the backend can't be swapped without a restart.
            _runtimeLibraryOrderInitialized = true;
            _loadedModelId = modelId;
            _selectedModelId = modelId;
            _host?.SetSetting("selectedModel", modelId);
            _accelerationStatus = cudaUnavailableDetail is null
                ? CreateLoadedAccelerationStatus(_computeBackend, _accelerationPreference)
                : CreateCudaUnavailableStatus(cudaUnavailableDetail);
            _host?.Log(
                PluginLogLevel.Info,
                $"Loaded model {modelId} using {_computeBackend.ToUpperInvariant()}"
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is null || _loadedModelId is null)
                throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

            var threshold = _noSpeechThreshold;

            var builder = _factory
                .CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                .WithNoSpeechThreshold(threshold);

            if (!string.IsNullOrWhiteSpace(prompt))
                builder.WithPrompt(prompt);

            if (translate)
                builder.WithTranslate();

            await using var processor = builder.Build();
            await using var audioStream = new MemoryStream(wavAudio, writable: false);

            var text = new StringBuilder();
            string? detectedLanguage = null;
            double durationSeconds = 0;
            float? noSpeechProbability = null;

            await foreach (var segment in processor.ProcessAsync(audioStream, ct))
            {
                if (
                    string.IsNullOrWhiteSpace(detectedLanguage)
                    && !string.IsNullOrWhiteSpace(segment.Language)
                )
                    detectedLanguage = segment.Language;

                durationSeconds = Math.Max(durationSeconds, segment.End.TotalSeconds);
                noSpeechProbability = segment.NoSpeechProbability;

                // whisper.cpp returns every segment, including ones it has
                // flagged as silence. Skip those so training-bias phrases like
                // "Thank you." don't leak into the output during silent gaps.
                if (segment.NoSpeechProbability > threshold)
                    continue;

                var segmentText = segment.Text.Trim();
                if (segmentText.Length > 0)
                {
                    if (text.Length > 0)
                        text.Append(' ');

                    text.Append(segmentText);
                }
            }

            return new PluginTranscriptionResult(
                text.ToString().Trim(),
                detectedLanguage,
                durationSeconds,
                noSpeechProbability
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadModelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeFactoryUnsafe();
            _loadedModelId = null;
            _selectedModelId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        await _gate.WaitAsync(ct);
        try
        {
            if (_loadedModelId == modelId)
            {
                DisposeFactoryUnsafe();
                _loadedModelId = null;
            }

            if (_selectedModelId == modelId)
            {
                _selectedModelId = null;
                _host?.SetSetting("selectedModel", "");
            }

            TryDeleteFile(modelPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        DisposeFactoryUnsafe();
        _gate.Dispose();
        _httpClient.Dispose();
    }

    // Provisions everything whisper.cpp's CUDA build needs, on demand:
    //   1. the CUDA math libraries (cudart + cuBLAS) it links against, preloaded
    //      RTLD_GLOBAL via the shared provisioner (downloaded if the host lacks them);
    //   2. the ~409 MB CUDA native build itself, fetched from nuget.org and cached
    //      rather than bundled in every package.
    // LoadModelAsync points RuntimeOptions.LibraryPath at the cache itself, but only
    // after its post-provision backend re-check confirms CUDA is still the chosen
    // backend — so a backend switch during this (slow) download can't leave that
    // process-wide path committed to a load we then abort. Throws on any failure so
    // the caller can fall back to a working CPU load.
    public async Task EnsureCudaRuntimeReadyAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "NVIDIA CUDA acceleration for whisper.cpp is only available on Linux x64."
            );

        _cudaProvisioner ??= new CudaRuntimeProvisioner(
            CudaRuntimeProvisioner.DefaultCacheRoot(),
            _httpClient,
            msg => _host?.Log(PluginLogLevel.Info, msg)
        );
        _whisperCudaInstaller ??= new WhisperCudaRuntimeInstaller(
            _host?.PluginAssetDirectory ?? ".",
            _httpClient,
            msg => _host?.Log(PluginLogLevel.Info, msg)
        );

        // The two stages map to a single monotonic 0→1 bar for the host: cudart + cuBLAS
        // are the bulk on a cold host, so weight them 70% and the native build 30%.
        // 1. Preload cudart + cuBLAS, downloading any the host doesn't provide.
        await _cudaProvisioner
            .EnsureReadyAsync(
                CudaRuntimeProfile.WhisperCublas,
                ProvisionProgress("CUDA libraries", progress, 0.0, 0.7),
                ct
            )
            .ConfigureAwait(false);

        // 2. Download + extract whisper.cpp's CUDA native build on first use.
        await _whisperCudaInstaller
            .EnsureInstalledAsync(
                ProvisionProgress("whisper.cpp GPU runtime", progress, 0.7, 1.0),
                ct
            )
            .ConfigureAwait(false);
    }

    // Logs download progress in coarse 10% steps (so a first-time multi-hundred-MB fetch
    // doesn't look hung, without flooding the log) AND forwards a fraction mapped into
    // [start, end] of the overall provisioning bar to the host's progress reporter, so
    // the UI can show a real download bar instead of a static spinner.
    private IProgress<double> ProvisionProgress(
        string label,
        IProgress<double>? forward,
        double start,
        double end
    )
    {
        var lastBucket = -1;
        return new Progress<double>(p =>
        {
            var clamped = Math.Clamp(p, 0, 1);
            var bucket = (int)(clamped * 10);
            if (bucket != lastBucket)
            {
                lastBucket = bucket;
                _host?.Log(PluginLogLevel.Info, $"{label}: {clamped:P0}");
            }
            forward?.Report(start + clamped * (end - start));
        });
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: NoSpeechThresholdKey,
                Label: Loc.L("Settings.NoSpeechThreshold"),
                Placeholder: DefaultNoSpeechThreshold.ToString(CultureInfo.InvariantCulture),
                Description: Loc.L("Settings.NoSpeechThresholdDescription"),
                Kind: PluginSettingKind.Text
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
    {
        if (key != NoSpeechThresholdKey)
            return Task.FromResult<string?>(null);

        var raw = _host?.GetSetting<string>(NoSpeechThresholdKey);
        return Task.FromResult<string?>(string.IsNullOrWhiteSpace(raw) ? null : raw);
    }

    public Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        if (key != NoSpeechThresholdKey)
            return Task.CompletedTask;

        if (string.IsNullOrWhiteSpace(value))
        {
            _host?.SetSetting(NoSpeechThresholdKey, string.Empty);
            _noSpeechThreshold = DefaultNoSpeechThreshold;
            return Task.CompletedTask;
        }

        if (
            !float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            || parsed is < 0f or > 1f
        )
        {
            // Persist the attempted value so ValidateAsync can read it back and
            // surface the rejection reason to the UI. Leave _noSpeechThreshold
            // at its last valid value so transcription keeps working.
            _host?.SetSetting(NoSpeechThresholdKey, value);
            return Task.CompletedTask;
        }

        _host?.SetSetting(
            NoSpeechThresholdKey,
            parsed.ToString(CultureInfo.InvariantCulture)
        );
        _noSpeechThreshold = parsed;
        return Task.CompletedTask;
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var raw = _host?.GetSetting<string>(NoSpeechThresholdKey);
        if (string.IsNullOrWhiteSpace(raw))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(
                    true,
                    Loc.L(
                        "Settings.UsingDefaultThreshold",
                        DefaultNoSpeechThreshold.ToString(CultureInfo.InvariantCulture)
                    )
                )
            );

        if (
            float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0f and <= 1f
        )
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(
                    true,
                    Loc.L(
                        "Settings.ThresholdSet",
                        parsed.ToString(CultureInfo.InvariantCulture)
                    )
                )
            );

        return Task.FromResult<PluginSettingsValidationResult?>(
            new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.ThresholdInvalid")
            )
        );
    }

    private ModelDefinition GetModel(string modelId) =>
        Models.FirstOrDefault(model => model.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private string GetModelPath(string modelId)
    {
        var host = _host ?? throw new InvalidOperationException("Plugin is not activated.");
        var model = GetModel(modelId);
        return Path.Join(host.PluginAssetDirectory, "Models", model.FileName);
    }

    private void DisposeFactoryUnsafe()
    {
        _factory?.Dispose();
        _factory = null;
    }

    // FromPath returns a factory even when whisper.cpp failed to create the native
    // context (Whisper.net stores a null context and defers the throw to CreateBuilder).
    // Probe it here: CreateBuilder is the cheapest public call that surfaces a null
    // context, and it allocates no native state (that happens at Build()), so the
    // discarded builder needs no disposal.
    private static bool TryValidateFactory(WhisperFactory factory)
    {
        try
        {
            _ = factory.CreateBuilder();
            return true;
        }
        catch (WhisperModelLoadException)
        {
            return false;
        }
    }

    private WhisperFactoryOptions CreateFactoryOptions() =>
        new()
        {
            UseGpu = string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase),
        };

    // Points Whisper.net's runtime order at the current _computeBackend. The order
    // is consulted only when the native library first loads and ignored for the
    // process lifetime afterwards, so this no-ops once the runtime is pinned.
    //
    // CUDA uses a single-entry [Cuda] order on purpose — NOT [Cuda, Cpu]. Whisper.net
    // only auto-falls-back to CPU when Cuda is the LAST entry; with Cuda last its
    // loader skips the CudaHelper.IsCudaAvailable() probe entirely (see
    // NativeLibraryLoader.IsRuntimeSupported). That probe P/Invokes the *unversioned*
    // libcudart.so, which the hosts this on-demand runtime targets do not have — they
    // only get our cached libcudart.so.12. So [Cuda, Cpu] would make IsCudaAvailable()
    // fail and silently downgrade every on-demand CUDA user to CPU. With [Cuda] the
    // probe is bypassed and our preloaded runtime loads. (We can't lean on an
    // in-process CPU fallback after a failed load anyway — see LoadModelAsync.)
    private void ApplyRuntimeLibraryOrderUnsafe()
    {
        if (_runtimeLibraryOrderInitialized)
            return;

        RuntimeOptions.RuntimeLibraryOrder = string.Equals(
            _computeBackend,
            "cuda",
            StringComparison.OrdinalIgnoreCase
        )
            ? [RuntimeLibrary.Cuda]
            : [RuntimeLibrary.Cpu];
    }

    // Test seam: simulate the native runtime having loaded and pinned itself to a
    // backend, without a real model file or WhisperFactory, so the CPU↔CUDA
    // restart-required status logic can be unit-tested.
    internal void MarkNativeRuntimeLoadedForTests(string backend)
    {
        var normalized = string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";
        _gate.Wait();
        try
        {
            _computeBackend = normalized;
            _runtimeLibraryOrderInitialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(
        TranscriptionAccelerationPreference preference
    )
    {
        return preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => new(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Preparing NVIDIA CUDA",
                "The GPU runtime downloads on the next model load."
            ),
            TranscriptionAccelerationPreference.Cpu => new(
                TranscriptionAccelerationBackend.Cpu,
                "Preparing CPU",
                "Will apply on next model load."
            ),
            _ => new(
                TranscriptionAccelerationBackend.Cpu,
                "Preparing acceleration",
                "Will apply on next model load."
            ),
        };
    }

    private TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        string loadedBackend,
        TranscriptionAccelerationPreference preference
    )
    {
        var loaded = string.Equals(loadedBackend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionAccelerationBackend.NvidiaCuda
            : TranscriptionAccelerationBackend.Cpu;

        var displayText =
            loaded == TranscriptionAccelerationBackend.NvidiaCuda
                ? "Using NVIDIA CUDA"
                : "Using CPU";

        var requestedBackend = preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda =>
                TranscriptionAccelerationBackend.NvidiaCuda,
            TranscriptionAccelerationPreference.Cpu => TranscriptionAccelerationBackend.Cpu,
            _ => loaded,
        };

        if (requestedBackend != loaded)
        {
            var detail = loaded == TranscriptionAccelerationBackend.Cpu
                ? "Process is pinned to CPU. Restart to switch to NVIDIA CUDA."
                : "Process is pinned to NVIDIA CUDA. Restart to switch to CPU.";
            return new TranscriptionAccelerationStatus(loaded, displayText, detail, true);
        }

        return new TranscriptionAccelerationStatus(loaded, displayText);
    }

    // An explicit NVIDIA CUDA request that couldn't be honoured (no usable GPU, a
    // failed runtime download, missing CUDA libraries): the model still loaded on
    // CPU, so report CPU as active and carry the reason for the UI to surface. The
    // native runtime is now pinned to CPU for the process, so retrying CUDA needs a
    // restart — flag it, matching the requested-vs-loaded mismatch path (a later
    // reload would otherwise re-derive RequiresRestart=true and flip this status).
    private static TranscriptionAccelerationStatus CreateCudaUnavailableStatus(string detail) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "Using CPU",
            $"CUDA unavailable: {detail}",
            RequiresRestart: true
        );

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private sealed record ModelDefinition(
        string Id,
        string DisplayName,
        GgmlType Type,
        QuantizationType Quantization,
        string FileName,
        string SizeDescription,
        long EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended
    );
}
