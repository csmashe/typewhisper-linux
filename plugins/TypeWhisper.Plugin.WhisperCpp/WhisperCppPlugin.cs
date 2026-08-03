// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.Plugin.WhisperCpp;

internal sealed record WhisperCppTranscriptionSegment(
    string Text,
    string? Language,
    TimeSpan End,
    float NoSpeechProbability
);

public sealed class WhisperCppPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string NoSpeechThresholdKey = "noSpeechThreshold";
    private const float DefaultNoSpeechThreshold = 0.6f;
    private static readonly IReadOnlyList<ModelDefinition> s_models =
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
    // streamed body, so a short ceiling would cancel these multi-hundred-MB fetches
    // mid-stream; use a generous 2 h ceiling and rely on the per-call token for
    // cancellation. ConnectTimeout bounds a socket that never establishes, and
    // ResilientDownloader's idle watchdog bounds a half-open socket mid-body to seconds.
    private readonly HttpClient _httpClient =
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromHours(2),
        };
    private IPluginHostServices? _host;
    private WhisperFactory? _factory;
    private CudaRuntimeProvisioner? _cudaProvisioner;
    private WhisperCudaRuntimeInstaller? _whisperCudaInstaller;
    private string? _loadedModelId;
    private string _computeBackend = "cpu";
    private bool _runtimeLibraryOrderInitialized;

    // The RuntimeLibraryOrder actually loaded for the process ("cpu" or "cuda"), set
    // once when the native runtime first pins. Distinct from _computeBackend, which is
    // the per-factory UseGpu choice and CAN differ: a [Cuda]-pinned runtime can run CPU
    // compute (UseGpu=false) with no reload. Restart-required logic reasons about THIS
    // (the native pin), not _computeBackend — only a [Cpu]-pinned runtime genuinely
    // needs a restart to reach CUDA. Set together with _runtimeLibraryOrderInitialized
    // at the single pin site so the two can never drift.
    private string? _pinnedRuntimeBackend;

    // Set when WhisperFactory.FromPath throws at the native LIBRARY-load layer.
    // Whisper.net caches that failure in a process-wide static Lazy, so once it
    // happens no later FromPath (on any backend) can succeed — the only recovery is
    // an app restart. We short-circuit subsequent loads instead of re-entering
    // FromPath and re-throwing Whisper.net's cached failure.
    private bool _nativeRuntimeLoadFailed;

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
    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
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

    public TranscriptionAccelerationPreference AccelerationPreference { get; private set; } = TranscriptionAccelerationPreference.Auto;

    public TranscriptionAccelerationStatus AccelerationStatus { get; private set; } = new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_models
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
        SelectedModelId = host.GetSetting<string>("selectedModel");
        _noSpeechThreshold = ReadNoSpeechThreshold(host);

        // Create the CUDA provisioner/installer eagerly so IsCudaRuntimeProvisioned can
        // report a warm cache immediately after a restart (the host gates CUDA selection
        // on it), not only after a download has been attempted. Both are cheap to build;
        // the ?? lets tests inject fakes before activate.
        InitializeCudaDependencies(host);

        host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    private void InitializeCudaDependencies(IPluginHostServices host)
    {
        _cudaProvisioner ??= new CudaRuntimeProvisioner(
            CudaRuntimeProvisioner.CacheRootForPluginAssetDirectory(
                host.PluginAssetDirectory
            ),
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg),
            // Resolved per call, not captured: the provisioner outlives a disable/re-enable
            // cycle, and the first activation's process scope is retired by then.
            () => _host?.Processes
                  ?? throw new NotSupportedException(
                      "The plugin host does not provide process supervision."
                  )
        );
        _whisperCudaInstaller ??= new WhisperCudaRuntimeInstaller(
            host.PluginAssetDirectory,
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );
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
        SelectedModelId = modelId;
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

            // Reason about the NATIVE pin (_pinnedRuntimeBackend), not the effective
            // compute. The [Cuda] .so set can build a UseGpu=false factory, and the
            // [Cpu] set can never load CUDA, so:
            //   - not pinned yet   → apply the choice; the first load pins it.
            //   - pinned to "cuda" → accept CPU or CUDA; just rebuild the factory with the
            //                        new UseGpu (no RuntimeLibraryOrder change needed).
            //   - pinned to "cpu"  → CPU stays fine; CUDA genuinely needs the [Cuda] .so
            //                        set, which can't load twice — refuse (restart only).
            if (_pinnedRuntimeBackend == "cpu" && normalized == "cuda")
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    "Cannot switch compute backend to 'cuda': the native runtime is pinned "
                        + "to CPU. Restart the app to load the CUDA runtime."
                );
                return false;
            }

            _computeBackend = normalized;
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
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
            _ => "cpu",
        };

        // Always record the host's last requested preference so the SDK getter
        // reflects user intent, even when the runtime can't honour it yet.
        AccelerationPreference = preference;

        AccelerationStatus = TryConfigureComputeBackend(backend)
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

            var tempPath = Path.Join(
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
            // The order ApplyRuntimeLibraryOrderUnsafe is about to apply (it reads
            // _computeBackend) IS the native pin. Capture it now, before the GPU-context
            // fallback below can downgrade _computeBackend to "cpu", so the pin records
            // "cuda" even when this load ends up running CPU compute.
            var appliedOrder = string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase)
                ? "cuda"
                : "cpu";
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
                // The one-shot native loader is poisoned for the process; only a restart
                // can recover, so this is genuinely restart-required (the pin isn't even
                // set yet here — it's recorded only after a successful validation below).
                AccelerationStatus = CreateCudaUnavailableStatus(
                    "The GPU runtime could not be loaded. Restart TypeWhisper to use CPU.",
                    requiresRestart: true
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
                // Effective compute is now CPU — harmless to record, since the pin is
                // tracked separately in _pinnedRuntimeBackend (= "cuda" here). Rebuild
                // with an explicit UseGpu=false rather than re-reading _computeBackend, so
                // the intent is local and can't be misread as a pin change.
                _computeBackend = "cpu";
                // Same pinned native runtime; only the context is rebuilt (UseGpu=false).
                _factory = WhisperFactory.FromPath(modelPath, CreateFactoryOptions(useGpu: false));
            }

            if (!TryValidateFactory(_factory))
            {
                DisposeFactoryUnsafe();
                throw new InvalidOperationException(
                    $"Failed to load whisper model '{modelId}': the model could not be initialized."
                );
            }

            // First successful factory creation loads + pins the native runtime. Record
            // the NATIVE order that was applied (appliedOrder, captured before any
            // GPU-context fallback downgraded _computeBackend) — distinct from the
            // effective compute. From here a CPU↔GPU toggle on a [Cuda]-pinned runtime
            // needs no restart; only [Cpu]→CUDA does (see TryConfigureComputeBackend).
            _pinnedRuntimeBackend ??= appliedOrder;
            _runtimeLibraryOrderInitialized = true;
            _loadedModelId = modelId;
            SelectedModelId = modelId;
            _host?.SetSetting("selectedModel", modelId);
            // Restart is required only if the process pinned the [Cpu] .so set (a
            // provisioning-failure downgrade). A GPU-context fallback pinned [Cuda], so CUDA
            // is reachable again by a reload — no restart (matches CreateLoadedAcceleration
            // Status / TryConfigureComputeBackend).
            AccelerationStatus = cudaUnavailableDetail is null
                ? CreateLoadedAccelerationStatus(_computeBackend, AccelerationPreference)
                : CreateCudaUnavailableStatus(
                    cudaUnavailableDetail,
                    requiresRestart: _pinnedRuntimeBackend == "cpu"
                );
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

            // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- local function kept near its point of use for readability.
            async IAsyncEnumerable<WhisperCppTranscriptionSegment> GetSegmentsAsync()
            {
                // AccumulateSegmentsAsync awaits this enumerable to completion before the
                // await-using scope exits, so processor/audioStream stay alive throughout.
                // ReSharper disable AccessToDisposedClosure
                await foreach (var segment in processor.ProcessAsync(audioStream, ct))
                {
                    yield return new WhisperCppTranscriptionSegment(
                        segment.Text,
                        segment.Language,
                        segment.End,
                        segment.NoSpeechProbability
                    );
                }
                // ReSharper restore AccessToDisposedClosure
            }

            return await AccumulateSegmentsAsync(GetSegmentsAsync(), threshold);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static async Task<PluginTranscriptionResult> AccumulateSegmentsAsync(
        IAsyncEnumerable<WhisperCppTranscriptionSegment> segments,
        float threshold
    )
    {
        var text = new StringBuilder();
        string? detectedLanguage = null;
        double durationSeconds = 0;
        float? noSpeechProbability = null;

        await foreach (var segment in segments)
        {
            if (
                string.IsNullOrWhiteSpace(detectedLanguage)
                && !string.IsNullOrWhiteSpace(segment.Language)
            )
                detectedLanguage = segment.Language;

            durationSeconds = Math.Max(durationSeconds, segment.End.TotalSeconds);

            // Min, so downstream silence checks trigger only when ALL segments are silent.
            noSpeechProbability = noSpeechProbability is null
                ? segment.NoSpeechProbability
                : Math.Min(noSpeechProbability.Value, segment.NoSpeechProbability);

            // whisper.cpp returns every segment, including ones it has
            // flagged as silence. Skip those so training-bias phrases like
            // "Thank you." don't leak into the output during silent gaps.
            if (segment.NoSpeechProbability > threshold)
                continue;

            var segmentText = segment.Text.Trim();
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
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

    public async Task UnloadModelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeFactoryUnsafe();
            _loadedModelId = null;
            SelectedModelId = null;
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

            if (SelectedModelId == modelId)
            {
                SelectedModelId = null;
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
            CudaRuntimeProvisioner.CacheRootForPluginAssetDirectory(
                _host?.PluginAssetDirectory
            ),
            _httpClient,
            msg => _host?.Log(PluginLogLevel.Info, msg),
            () => _host?.Processes
                  ?? throw new NotSupportedException(
                      "The plugin host does not provide process supervision."
                  )
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

    public async Task ClearCudaRuntimeAsync(CancellationToken ct)
    {
        // Defensive: dispose the factory so the native runtime isn't needlessly held
        // while we delete the cache. (On Linux the .so files can be unlinked even while
        // loaded, so a restart is still required for the fresh re-download to take
        // effect — but releasing the factory first keeps the on-disk state clean.)
        // NOT UnloadModelAsync(): the host clears every provisioning engine, so this must
        // not deselect whisper.cpp's model as a side effect when it isn't the active
        // engine. Drop only the live factory and preserve _selectedModelId.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DisposeFactoryUnsafe();
            _loadedModelId = null;
        }
        finally
        {
            _gate.Release();
        }

        // Clear BOTH whisper.cpp's CUDA build and the shared CUDA math-library cache, even
        // if the first delete fails — the actually-corrupt one might be the shared set
        // (the latter is shared with sherpa-onnx; deleting it again from that plugin is an
        // idempotent no-op). Aggregate non-cancel faults and throw once both attempts have
        // run; propagate cancellation immediately.
        var failures = new List<string>();

        try
        {
            if (_whisperCudaInstaller is not null)
                await _whisperCudaInstaller.ClearCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add($"whisper.cpp GPU runtime: {ex.Message}");
        }

        try
        {
            if (_cudaProvisioner is not null)
                await _cudaProvisioner.ClearCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add($"shared CUDA runtime: {ex.Message}");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Failed to clear whisper.cpp CUDA runtime cache: " + string.Join("; ", failures)
            );
    }

    // Logs download progress in coarse 10% steps (so a first-time multi-hundred-MB fetch
    // doesn't look hung, without flooding the log) AND forwards a fraction mapped into
    // [start, end] of the overall provisioning bar to the host's progress reporter, so
    // the UI can show a real download bar instead of a static spinner.
    private Progress<double> ProvisionProgress(
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
        return Task.FromResult(string.IsNullOrWhiteSpace(raw) ? null : raw);
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

    private static ModelDefinition GetModel(string modelId) =>
        s_models.FirstOrDefault(model => model.Id == modelId)
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

    private static WhisperFactoryOptions CreateFactoryOptions(bool useGpu) =>
        new() { UseGpu = useGpu };

    private WhisperFactoryOptions CreateFactoryOptions() =>
        CreateFactoryOptions(
            string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase)
        );

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
    // restart-required status logic can be unit-tested. effectiveCompute defaults to the
    // pin but can differ for a [Cuda]-pinned runtime running CPU compute (the first-load
    // GPU-context-failure case): MarkNativeRuntimeLoadedForTests("cuda", "cpu").
    internal void MarkNativeRuntimeLoadedForTests(
        string pinnedBackend,
        string? effectiveCompute = null
    )
    {
        var pin = string.Equals(pinnedBackend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";
        var effective =
            effectiveCompute is null
                ? pin
                : string.Equals(effectiveCompute, "cuda", StringComparison.OrdinalIgnoreCase)
                    ? "cuda"
                    : "cpu";
        _gate.Wait();
        try
        {
            _pinnedRuntimeBackend = pin;
            _computeBackend = effective;
            _runtimeLibraryOrderInitialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Test seam: simulate Whisper.net's one-shot native LIBRARY load having FAILED and
    // poisoned its process-wide static loader, so the next LoadModelAsync short-circuits
    // with the restart-required message instead of re-entering FromPath.
    internal void MarkNativeRuntimeLoadFailedForTests()
    {
        _gate.Wait();
        try
        {
            _nativeRuntimeLoadFailed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Test seam: pre-seed the CUDA provisioner + installer with fakes before ActivateAsync
    // (whose ??= lazy-create then skips), so the LoadModelAsync provisioning/fallback state
    // machine can be driven without a network or GPU.
    internal void SetCudaDependenciesForTests(
        CudaRuntimeProvisioner provisioner,
        WhisperCudaRuntimeInstaller installer
    )
    {
        _cudaProvisioner = provisioner;
        _whisperCudaInstaller = installer;
    }

    // Test seam: exercise the same eager construction path as ActivateAsync without
    // invoking unrelated activation work.
    internal void InitializeCudaDependenciesForTests(IPluginHostServices host)
    {
        _host = host;
        InitializeCudaDependencies(host);
    }

    internal string? CudaRuntimeCacheRootForTests =>
        _cudaProvisioner is null
            ? null
            : Directory.GetParent(_cudaProvisioner.CacheDirectory)?.FullName;

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(
        TranscriptionAccelerationPreference preference
    )
    {
        return preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Preparing NVIDIA CUDA",
                "The GPU runtime downloads on the next model load."
            ),
            TranscriptionAccelerationPreference.Cpu => new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Preparing CPU",
                "Will apply on next model load."
            ),
            _ => new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Preparing acceleration",
                "Will apply on next model load."
            ),
        };
    }

    // Status for the user's current EFFECTIVE compute (effectiveBackend = _computeBackend:
    // whether they're actually getting GPU speed). RequiresRestart is derived from the
    // NATIVE pin, not the effective compute: only a [Cpu]-pinned process can't reach CUDA
    // without a restart. A [Cuda]-pinned runtime currently running CPU compute therefore
    // reports ActiveBackend=Cpu with no restart flag — a CUDA preference just triggers a
    // reload, which the caller can do.
    private TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        string effectiveBackend,
        TranscriptionAccelerationPreference preference
    )
    {
        var active = string.Equals(effectiveBackend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionAccelerationBackend.NvidiaCuda
            : TranscriptionAccelerationBackend.Cpu;

        var displayText =
            active == TranscriptionAccelerationBackend.NvidiaCuda
                ? "Using NVIDIA CUDA"
                : "Using CPU";

        // The ONLY toggle that needs a restart is loading the [Cuda] .so set on a process
        // pinned to [Cpu]. CPU↔GPU on a [Cuda] pin, and CPU on a [Cpu] pin, are reloads.
        var requiresRestart =
            _pinnedRuntimeBackend == "cpu"
            && preference == TranscriptionAccelerationPreference.NvidiaCuda;

        if (requiresRestart)
            return new TranscriptionAccelerationStatus(
                active,
                displayText,
                "Process is pinned to CPU. Restart to switch to NVIDIA CUDA.",
                true
            );

        return new TranscriptionAccelerationStatus(active, displayText);
    }

    // An explicit NVIDIA CUDA request that couldn't be honoured (no usable GPU, a failed
    // runtime download, missing CUDA libraries, or a GPU-context failure): the model still
    // loaded on CPU, so report CPU active and carry the reason for the UI. requiresRestart
    // is decided by the caller from the NATIVE pin — a [Cpu]-pinned process (provisioning
    // or library-load failure) genuinely needs a restart to reach CUDA, but a [Cuda]-pinned
    // process that merely fell back to CPU compute (GPU-context failure) can retry CUDA with
    // a reload, so it must NOT claim a restart is required (the whole point of M6).
    private static TranscriptionAccelerationStatus CreateCudaUnavailableStatus(
        string detail,
        bool requiresRestart
    ) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "Using CPU",
            $"CUDA unavailable: {detail}",
            RequiresRestart: requiresRestart
        );

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            //nada
        }
    }

    private sealed record ModelDefinition(
        string Id,
        string DisplayName,
        GgmlType Type,
        QuantizationType Quantization,
        string FileName,
        string SizeDescription,
        // ReSharper disable once InconsistentNaming -- MB (megabyte) is the correct unit; the suggested Mb means megabit.
        long EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended
    );
}
