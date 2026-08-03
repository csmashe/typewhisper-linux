using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;
using TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.Plugins.Shared.Net;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

public sealed class SherpaOnnxPlugin : ITranscriptionEnginePlugin
{
    private const string ParakeetRepo =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";
    private const string CanaryRepo =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";

    private static readonly IReadOnlyList<string> s_canarySupportedLanguages =
    [
        "en",
        "de",
        "fr",
        "es",
    ];

    // Native-library parse diagnostics from org.k2fsa.sherpa.onnx 1.12.23. Match only
    // these artifact-specific signatures; generic InvalidOperationException failures
    // (CUDA/provider/runtime setup) must leave the downloaded model intact.
    private static readonly string[] s_invalidModelLoadMessageFragments =
    [
        "INVALID_PROTOBUF",
        "INVALID_GRAPH",
        "Failed to load model because protobuf parsing failed",
        "Protobuf parsing failed",
        "ModelProto does not have a graph",
        "model format error",
        "Missing opset in the model",
        "number of lines in tokens.txt",
        "tokens.txt does not include the blank token",
        "We expect that tokens.txt contains the symbol",
        "Error when reading tokens",
        "tokens.size()",
        " != output_size:",
    ];

    private static readonly IReadOnlyList<ModelDefinition> s_models =
    [
        new(
            "parakeet-tdt-0.6b",
            "Parakeet TDT 0.6B",
            "~670 MB",
            670,
            25,
            true,
            false,
            true,
            [
                new ModelFileDefinition("encoder.int8.onnx", $"{ParakeetRepo}/encoder.int8.onnx", 652),
                new ModelFileDefinition("decoder.int8.onnx", $"{ParakeetRepo}/decoder.int8.onnx", 12),
                new ModelFileDefinition("joiner.int8.onnx", $"{ParakeetRepo}/joiner.int8.onnx", 6),
                new ModelFileDefinition("tokens.txt", $"{ParakeetRepo}/tokens.txt", 1),
            ]
        ),
        new(
            "canary-180m-flash",
            "Canary 180M Flash",
            "~198 MB",
            198,
            4,
            false,
            true,
            false,
            [
                new ModelFileDefinition("encoder.int8.onnx", $"{CanaryRepo}/encoder.int8.onnx", 127),
                new ModelFileDefinition("decoder.int8.onnx", $"{CanaryRepo}/decoder.int8.onnx", 71),
                new ModelFileDefinition("tokens.txt", $"{CanaryRepo}/tokens.txt", 1),
            ]
        ),
    ];

    private readonly Lock _sync = new();

    // Drives the model-file downloads and the on-demand CUDA runtime fetches (the
    // ~224 MB sherpa tarball plus CUDA wheels up to ~685 MB). HttpClient.Timeout bounds
    // the WHOLE request including the streamed body, so a short ceiling would cancel these
    // large fetches mid-stream; use a generous 2 h ceiling and rely on the per-call token
    // for cancellation. ConnectTimeout bounds a socket that never establishes, and
    // ResilientDownloader's idle watchdog bounds a half-open socket mid-body to seconds.
    private readonly HttpClient _httpClient =
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromHours(2),
        };
    private IPluginHostServices? _host;
    private OfflineRecognizer? _recognizer;
    private Func<string, string, OfflineRecognizer> _parakeetRecognizerFactory =
        CreateParakeetRecognizer;
    private SherpaCudaRuntimeInstaller? _cudaRuntimeInstaller;
    private CudaRuntimeProvisioner? _cudaProvisioner;
    private string? _loadedModelId;
    private string? _loadedModelDir;
    private string _computeBackend = "cpu";

    // The WIRED ORT native runtime, pinned to whichever loads first in the process
    // ("cuda" once the CUDA ORT runtime is wired in, else "cpu"). It can't be
    // hot-swapped, so reaching CUDA from a CPU-only-wired runtime requires a restart.
    // Distinct from the recognizer's active provider (_computeBackend): a CUDA-wired
    // runtime can still run a CPU recognizer, and swapping between them needs no restart.
    private string? _loadedNativeProvider;

    // True once ConfigureCudaRuntime has wired the CUDA ORT runtime into the process (a
    // second libonnxruntime.so dlopen'd RTLD_GLOBAL + the import resolver redirected).
    // Lets a first-load CUDA-recognizer failure pin "cuda" (the runtime is CUDA-capable)
    // rather than "cpu", so a later CPU↔CUDA recognizer swap doesn't read as restart-required.
    private bool _cudaOrtRuntimeWired;

    private string _canarySrcLang = "en";
    private string _canaryTgtLang = "en";

    public string PluginId => "com.typewhisper.sherpa-onnx";
    public string PluginName => "Local Models (sherpa-onnx)";
    public string PluginVersion => "1.0.0";

    public string ProviderId => "sherpa-onnx";
    public string ProviderDisplayName => "Lokal (sherpa-onnx)";
    public bool IsConfigured => true;
    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => SelectedModelId == "canary-180m-flash";
    public bool SupportsModelDownload => true;

    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];

    // LoadModelAsync downloads + preloads the CUDA runtime on demand and falls back to
    // CPU itself, so the host need not require a system CUDA install for explicit CUDA.
    public bool ProvisionsCudaRuntimeOnDemand => true;

    // CUDA is ready only when BOTH the math libraries (system-or-cached) AND the
    // sherpa-onnx GPU native build are present. A partial state (e.g. math libs cached
    // but the GPU tarball not yet extracted) reports false so the host keeps offering
    // the download action. Pure file/cache inspection — no driver probe, no download.
    public bool IsCudaRuntimeProvisioned =>
        _cudaProvisioner?.IsProfileSatisfied(CudaRuntimeProfile.OnnxRuntimeCuda) == true
        && _cudaRuntimeInstaller?.IsInstalled == true;

    public TranscriptionAccelerationPreference AccelerationPreference { get; private set; } = TranscriptionAccelerationPreference.Auto;

    public TranscriptionAccelerationStatus AccelerationStatus { get; private set; } = new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_models
            .Select(m => new PluginModelInfo(m.Id, m.DisplayName)
            {
                SizeDescription = m.SizeDescription,
                EstimatedSizeMB = m.EstimatedSizeMB,
                IsRecommended = m.IsRecommended,
                LanguageCount = m.LanguageCount,
            })
            .ToList();

    public IReadOnlyList<string> SupportedLanguages =>
        SelectedModelId == "canary-180m-flash" ? s_canarySupportedLanguages : [];

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;

        // Lazily provisioned on demand; the ?? lets tests inject fakes before activate.
        InitializeCudaDependencies(host);

        // Register the import resolver now; until CUDA is configured it defers to
        // the default loader, which picks up the CPU runtime from the managed nuget.
        SherpaOnnxNativeRuntime.RegisterResolver();

        MigrateModelFiles();
        return Task.CompletedTask;
    }

    private void InitializeCudaDependencies(IPluginHostServices host)
    {
        _cudaRuntimeInstaller ??= new SherpaCudaRuntimeInstaller(
            host.PluginAssetDirectory,
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );
        _cudaProvisioner ??= new CudaRuntimeProvisioner(
            CudaRuntimeProvisioner.CacheRootForPluginAssetDirectory(
                host.PluginAssetDirectory
            ),
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );
    }

    public Task DeactivateAsync()
    {
        UnloadRecognizer();
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        SelectedModelId = modelId;
    }

    public Task ConfigureComputeBackendAsync(string backend)
    {
        var normalized = string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";

        // Serialize backend switches with model load/unload: without the lock,
        // a LoadModelAsync running on another thread could observe the old
        // backend, pass its check, and then load against a recognizer that's
        // been unloaded mid-flight.
        lock (_sync)
        {
            if (_computeBackend == normalized)
                return Task.CompletedTask;

            // Reason about the WIRED native runtime (_loadedNativeProvider), not the
            // recognizer's active provider. A CUDA-wired runtime accepts CPU↔CUDA
            // recognizer swaps with no restart; only a CPU-only-wired runtime needs a
            // restart to reach CUDA. Refuse just that one case so it surfaces as
            // restart-required via the acceleration status (and a reload can't silently
            // rebuild on a backend the runtime can't provide).
            if (
                string.Equals(_loadedNativeProvider, "cpu", StringComparison.Ordinal)
                && string.Equals(normalized, "cuda", StringComparison.Ordinal)
            )
                return Task.CompletedTask;

            _computeBackend = normalized;
            // Drop the active recognizer so the next load rebuilds on the new backend.
            UnloadRecognizerUnsafe();
        }

        return Task.CompletedTask;
    }

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        AccelerationPreference = preference;

        var desired = preference == TranscriptionAccelerationPreference.NvidiaCuda ? "cuda" : "cpu";

        // ConfigureComputeBackendAsync completes synchronously for SherpaOnnx (no
        // awaits in the body) and refuses to switch once the native provider is
        // pinned. Derive the status from the saved preference so a pinned mismatch
        // reads as restart-required and survives a subsequent reload (which would
        // otherwise overwrite it). The CUDA runtime is provisioned lazily on the
        // next LoadModelAsync.
        _ = ConfigureComputeBackendAsync(desired);
        AccelerationStatus = _loadedNativeProvider is null
            ? CreatePendingAccelerationStatus(preference)
            // Pass the EFFECTIVE provider (_computeBackend) for the "active backend"; the
            // restart flag is derived from the wired runtime inside the helper.
            : CreateLoadedAccelerationStatus(_computeBackend, preference);
    }

    public bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        return model.Files.All(f => File.Exists(Path.Join(dir, f.FileName)));
    }

    public Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        _ = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);

        lock (_sync)
        {
            if (_loadedModelId == modelId)
                UnloadRecognizerUnsafe();

            if (SelectedModelId == modelId)
                SelectedModelId = null;
        }

        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }

    public async Task DownloadModelAsync(
        string modelId,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);

        var totalBytes = model.Files.Sum(f => (long)f.EstimatedSizeMB * 1024 * 1024);
        long cumulativeBytesRead = 0;
        // Single throttle across all files (MinValue so the first report always fires).
        var lastReport = DateTime.MinValue;

        foreach (var file in model.Files)
        {
            var filePath = Path.Join(dir, file.FileName);
            if (File.Exists(filePath))
            {
                // Credit an already-downloaded file's size so a resumed multi-file
                // download starts the bar where it really is instead of at 0.
                cumulativeBytesRead += new FileInfo(filePath).Length;
                continue;
            }

            // Model files have no published checksum, so resume can't be made safe.
            // allowResume:false still gives the idle/connect watchdog (a stall aborts and
            // restarts clean instead of hanging) but each file re-downloads from zero.
            long fileOnDisk = 0;
            await ResilientDownloader.DownloadToFileAsync(
                _httpClient,
                file.DownloadUrl,
                filePath,
                approxTotalBytes: null,
                idleTimeout: TimeSpan.FromSeconds(60),
                allowResume: false,
                onBytesOnDisk: onDisk =>
                {
                    fileOnDisk = onDisk;
                    var now = DateTime.UtcNow;
                    // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                    if ((now - lastReport).TotalMilliseconds > 250 && totalBytes > 0)
                    {
                        // Clamp: real on-disk sizes sum against an estimated total, so a
                        // slight overshoot past 1.0 is possible.
                        progress?.Report(
                            Math.Min(1.0, (double)(cumulativeBytesRead + onDisk) / totalBytes)
                        );
                        lastReport = now;
                    }
                },
                verifyComplete: path =>
                    VerifyModelArtifact(path, file.FileName, model.RequiresBlankToken),
                ct
            );

            cumulativeBytesRead += fileOnDisk;
        }

        progress?.Report(1.0);
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct) =>
        LoadModelAsync(modelId, null, ct);

    public async Task LoadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);

        if (!model.Files.All(f => File.Exists(Path.Join(dir, f.FileName))))
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        string desiredProvider;
        lock (_sync)
            desiredProvider = _computeBackend;

        // Set if a CUDA attempt fails and we fall back to CPU, so the final status
        // can explain why. We can't tell whether the user's saved setting was Auto
        // (the host resolves it before calling us and never passes Auto), so rather
        // than hard-fail an explicit CUDA request we always fall back to a working
        // CPU load and surface the reason in the acceleration status.
        string? cudaUnavailableDetail = null;

        // Provision + wire the GPU runtime before touching the recognizer. Done
        // outside the lock because it may download hundreds of MB on first use.
        if (string.Equals(desiredProvider, "cuda", StringComparison.Ordinal))
        {
            try
            {
                await EnsureCudaRuntimeReadyAsync(progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"sherpa-onnx CUDA runtime unavailable ({ex.Message}); falling back to CPU."
                        + (_cudaProvisioner is { } provisioner
                            ? $" Shared CUDA runtime cache: {provisioner.CacheDirectory}."
                            : "")
                );
                cudaUnavailableDetail = ex.Message;
                desiredProvider = "cpu";
                lock (_sync)
                    _computeBackend = "cpu";
            }
        }

        try
        {
            await Task.Run(
                () =>
                {
                    lock (_sync)
                    {
                        // Provisioning can take minutes; the backend may have been
                        // switched out from under us in that window. Abort the stale
                        // load rather than pinning the process to a runtime the user
                        // no longer wants (and possibly after downloading it for nothing).
                        if (!string.Equals(_computeBackend, desiredProvider, StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "Compute backend changed during model load; reload to apply the new backend."
                            );

                        // Revalidate cached/pre-fix artifacts before the native loader
                        // (guarantees below, at VerifyModelArtifact).
                        UnloadRecognizerUnsafe();
                        VerifyModelArtifacts(model, dir);

                        var activeProvider = desiredProvider;
                        try
                        {
                            _recognizer = model.SupportsTranslation
                                ? CreateCanaryRecognizer(dir, "en", "en", activeProvider)
                                : _parakeetRecognizerFactory(dir, activeProvider);
                        }
                        catch (Exception ex)
                            when (string.Equals(activeProvider, "cuda", StringComparison.Ordinal))
                        {
                            // Recreate with the CPU execution provider. The GPU ONNX
                            // Runtime is already wired in by ConfigureCudaRuntime and runs
                            // the CPU provider correctly, so this yields working CPU
                            // transcription rather than failing the load outright.
                            _host?.Log(
                                PluginLogLevel.Warning,
                                $"sherpa-onnx CUDA recognizer creation failed ({ex.Message}); falling back to CPU."
                            );
                            cudaUnavailableDetail = ex.Message;
                            activeProvider = "cpu";
                            _computeBackend = "cpu";
                            _recognizer = model.SupportsTranslation
                                ? CreateCanaryRecognizer(dir, "en", "en", activeProvider)
                                : _parakeetRecognizerFactory(dir, activeProvider);
                        }

                        // First successful load pins the native runtime for the process.
                        // Record the WIRED runtime (CUDA-capable vs CPU-only), not the
                        // recognizer's active provider: a CUDA-wired runtime whose recognizer
                        // fell back to CPU is still CUDA-capable, so it pins "cuda" and a later
                        // CPU↔CUDA swap needs no restart.
                        _loadedNativeProvider ??= _cudaOrtRuntimeWired ? "cuda" : activeProvider;

                        _loadedModelId = modelId;
                        _loadedModelDir = dir;
                        SelectedModelId = modelId;
                        _canarySrcLang = "en";
                        _canaryTgtLang = "en";
                        // Restart is required only if the wired runtime is CPU-only (a
                        // provisioning failure). A CUDA-wired runtime whose recognizer fell back
                        // to CPU pins "cuda" above, so CUDA is reachable again by a reload — no
                        // restart (matches CreateLoadedAccelerationStatus / the swap logic).
                        AccelerationStatus = cudaUnavailableDetail is null
                            ? CreateLoadedAccelerationStatus(activeProvider, AccelerationPreference)
                            : CreateCudaUnavailableStatus(
                                cudaUnavailableDetail,
                                requiresRestart: string.Equals(
                                    _loadedNativeProvider,
                                    "cpu",
                                    StringComparison.Ordinal
                                )
                            );

                        Debug.WriteLine(
                            $"[SherpaOnnx] Model {modelId} loaded from {dir} ({activeProvider})"
                        );
                    }
                },
                ct
            )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsArtifactInvalidLoadFailure(ex))
        {
            DeleteInvalidModelArtifacts(model, dir, ex);
            throw;
        }
    }

    public async Task EnsureCudaRuntimeReadyAsync(IProgress<double>? progress, CancellationToken ct)
    {
        EnsureCudaPlatformSupported();

        var provisioner =
            _cudaProvisioner
            ?? throw new InvalidOperationException("CUDA provisioner is not initialized.");
        var installer =
            _cudaRuntimeInstaller
            ?? throw new InvalidOperationException("CUDA runtime installer is not initialized.");

        // The two download stages map to a single monotonic 0→1 bar for the host: the
        // math libs (cuDNN alone is ~1.7 GB unpacked) are the bulk, so weight them 60%
        // and the sherpa GPU build 40%.
        // Preload the CUDA math libraries (cudart/cublas/cufft/curand/nvrtc/cuDNN),
        // downloading any the host doesn't already provide.
        await provisioner
            .EnsureReadyAsync(
                CudaRuntimeProfile.OnnxRuntimeCuda,
                ProvisionProgress("CUDA libraries", progress, 0.0, 0.6),
                ct
            )
            .ConfigureAwait(false);

        // Download + extract the sherpa-onnx GPU native build.
        await installer
            .EnsureInstalledAsync(
                ProvisionProgress("sherpa-onnx GPU runtime", progress, 0.6, 1.0),
                ct
            )
            .ConfigureAwait(false);

        // Route the managed bindings to the GPU dir and preload its ORT deps — but only
        // while it is still safe to do so. ConfigureCudaRuntime dlopens a second
        // libonnxruntime.so RTLD_GLOBAL and redirects the import resolver, which must
        // happen "before the first recognizer is created" (see SherpaOnnxNativeRuntime).
        // Don't wire the GPU ORT into a process that no longer wants CUDA — it would leave
        // a mixed CPU/GPU native state until restart. Two cases this guards against:
        //   1. A recognizer already pinned the process to CPU (the host's "download CUDA
        //      runtime" button, clicked while running on CPU).
        //   2. The user switched to CPU during the (slow) download, so the desired backend
        //      (_computeBackend) is no longer "cuda" — the LoadModelAsync re-check would
        //      abort the load anyway. Revalidate _computeBackend HERE, under _sync, right
        //      before wiring, since the check happens after a long unlocked download.
        // In both cases the files are now on disk and the host's restart prompt lets a fresh
        // process wire the GPU runtime cleanly. _cudaOrtRuntimeWired is set only after this
        // revalidation passes and ConfigureCudaRuntime actually wires the runtime.
        bool canWireIntoProcess;
        lock (_sync)
            canWireIntoProcess =
                (_loadedNativeProvider is null
                    || string.Equals(_loadedNativeProvider, "cuda", StringComparison.Ordinal))
                && string.Equals(_computeBackend, "cuda", StringComparison.Ordinal);

        if (canWireIntoProcess)
        {
            SherpaOnnxNativeRuntime.ConfigureCudaRuntime(installer.RuntimeDirectory);
            // The CUDA ORT runtime is now wired into the process. Record it so a later
            // recognizer that falls back to CPU still pins "cuda" (the runtime stays
            // CUDA-capable) and a CPU↔CUDA swap isn't misreported as restart-required.
            lock (_sync)
                _cudaOrtRuntimeWired = true;
        }
    }

    public async Task ClearCudaRuntimeAsync(CancellationToken ct)
    {
        // Defensive: drop any live recognizer so its native handles aren't needlessly
        // held while we delete the cache. (On Linux the .so files can be unlinked even
        // while mapped, so a restart is still required for the fresh re-download to take
        // effect — but releasing the recognizer first keeps the on-disk state clean.)
        // UnloadRecognizer does NOT touch _selectedModelId, so cache repair leaves the
        // user's model selection intact.
        UnloadRecognizer();

        // Clear BOTH the sherpa-onnx GPU build and the shared CUDA math-library cache,
        // even if the first delete fails — the actually-corrupt one might be the shared
        // set (the latter is shared with whisper.cpp; deleting it again from that plugin
        // is an idempotent no-op). Aggregate non-cancel faults and throw once both
        // attempts have run; propagate cancellation immediately.
        var failures = new List<string>();

        try
        {
            if (_cudaRuntimeInstaller is not null)
                await _cudaRuntimeInstaller.ClearCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add($"sherpa-onnx GPU runtime: {ex.Message}");
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
                "Failed to clear sherpa-onnx CUDA runtime cache: " + string.Join("; ", failures)
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

    private static void EnsureCudaPlatformSupported()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "NVIDIA CUDA acceleration for sherpa-onnx is only available on Linux x64."
            );
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        return Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                var audioSamples = DecodeWav(wavAudio);
                var audioDuration =
                    audioSamples.Length / (double)SherpaDecodeCoordinator.SampleRate;
                ct.ThrowIfCancellationRequested();

                lock (_sync)
                {
                    if (_recognizer is null || _loadedModelId is null)
                        throw new InvalidOperationException(
                            "Kein Modell geladen. LoadModelAsync zuerst aufrufen."
                        );

                    var model = GetModelDefinition(_loadedModelId);

                    if (model.SupportsTranslation)
                        EnsureCanaryLanguage(language, translate);

                    var coordinator = new SherpaDecodeCoordinator(chunk =>
                    {
                        using var stream = _recognizer.CreateStream();
                        stream.AcceptWaveform(SherpaDecodeCoordinator.SampleRate, chunk);
                        ct.ThrowIfCancellationRequested();
                        _recognizer.Decode(stream);
                        ct.ThrowIfCancellationRequested();
                        return stream.Result.Text;
                    });
                    var decoded = coordinator.Decode(
                        audioSamples,
                        model.SupportsTranslation,
                        ct
                    );

                    ct.ThrowIfCancellationRequested();
                    return new PluginTranscriptionResult(
                        decoded.Text,
                        decoded.DetectedLanguage,
                        audioDuration,
                        NoSpeechProbability: null
                    );
                }
            },
            ct
        );
    }

    public void Dispose()
    {
        UnloadRecognizer();
        _httpClient.Dispose();
    }

    private string GetModelDirectory(string modelId)
    {
        // Defense in depth: callers validate modelId against the known model list
        // first, but a model ID flows into a filesystem path here, so strip any path
        // separators and reject empty/relative segments before joining.
        var safeModelId = Path.GetFileName(modelId);
        if (string.IsNullOrWhiteSpace(safeModelId) || safeModelId is "." or "..")
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));

        return Path.Join(_host?.PluginAssetDirectory ?? ".", "Models", safeModelId);
    }

    // Test seam: simulate the process having wired its native ORT runtime, without
    // creating a real recognizer, so the CPU↔CUDA restart-required status logic can be
    // unit-tested. effectiveProvider defaults to the wired runtime but can differ for a
    // CUDA-wired runtime running a CPU recognizer (the first-load CUDA-recognizer-failure
    // case): MarkNativeRuntimeLoadedForTests("cuda", effectiveProvider: "cpu").
    internal void MarkNativeRuntimeLoadedForTests(
        string wiredRuntime,
        string? effectiveProvider = null
    )
    {
        var wired = string.Equals(wiredRuntime, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";
        var effective =
            effectiveProvider is null
                ? wired
                : string.Equals(effectiveProvider, "cuda", StringComparison.OrdinalIgnoreCase)
                    ? "cuda"
                    : "cpu";
        lock (_sync)
        {
            _loadedNativeProvider = wired;
            _cudaOrtRuntimeWired = wired == "cuda";
            _computeBackend = effective;
        }
    }

    // Test seam: pre-seed the CUDA provisioner + installer with fakes before ActivateAsync
    // (whose ??= lazy-create then skips), so the LoadModelAsync provisioning/fallback state
    // machine can be driven without a network or GPU.
    internal void SetCudaDependenciesForTests(
        CudaRuntimeProvisioner provisioner,
        SherpaCudaRuntimeInstaller installer
    )
    {
        _cudaProvisioner = provisioner;
        _cudaRuntimeInstaller = installer;
    }

    // Test seam: exercise the same eager construction path as ActivateAsync without
    // running the legacy model-file migration against a real per-user directory.
    internal void InitializeCudaDependenciesForTests(IPluginHostServices host) =>
        InitializeCudaDependencies(host);

    internal string? CudaRuntimeCacheRootForTests =>
        _cudaProvisioner is null
            ? null
            : Directory.GetParent(_cudaProvisioner.CacheDirectory)?.FullName;

    // Test seam: inject a throwing recognizer factory so native-load-failure
    // classification can be exercised without a real model, native runtime, or GPU.
    internal void SetParakeetRecognizerFactoryForTests(
        Func<string, string, OfflineRecognizer> factory
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        _parakeetRecognizerFactory = factory;
    }

    // Avoid ActivateAsync's one-shot migration probe in filesystem-isolated load tests.
    internal void SetHostForTests(IPluginHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    // Test seam: run the structural preflight without the native loader, so per-model
    // token/ONNX acceptance (e.g. Canary carries no blank token) is testable in isolation.
    internal static void RunArtifactPreflightForTests(string modelId, string modelDir) =>
        VerifyModelArtifacts(GetModelDefinition(modelId), modelDir);

    internal string ComputeBackendForTests
    {
        get
        {
            lock (_sync)
                return _computeBackend;
        }
    }

    // Test seam: exercise the production lock boundary with a managed delegate, so
    // cancellation and lock release need no native runtime.
    internal SherpaDecodeResult RunDecodeTransactionForTests(
        float[] audioSamples,
        bool parseCanaryPayload,
        SherpaDecodeDelegate decode,
        CancellationToken ct
    )
    {
        lock (_sync)
            return new SherpaDecodeCoordinator(decode).Decode(
                audioSamples,
                parseCanaryPayload,
                ct
            );
    }

    private static ModelDefinition GetModelDefinition(string modelId) =>
        s_models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void UnloadRecognizer()
    {
        lock (_sync)
            UnloadRecognizerUnsafe();
    }

    private void UnloadRecognizerUnsafe()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _loadedModelId = null;
        _loadedModelDir = null;
        _canarySrcLang = "en";
        _canaryTgtLang = "en";
    }

    private static void VerifyModelArtifacts(ModelDefinition model, string modelDir)
    {
        foreach (var file in model.Files)
            VerifyModelArtifact(
                Path.Join(modelDir, file.FileName),
                file.FileName,
                model.RequiresBlankToken
            );
    }

    // Artifact guarantees:
    //   *.onnx     — non-empty, well-framed top-level protobuf with a positive ONNX
    //                IR version and a non-empty GraphProto field. The graph's declared
    //                byte range must fit inside the file, which detects clean-EOF
    //                truncation without hashing or parsing hundreds of MB of tensors.
    //   tokens.txt — non-empty, strict UTF-8 token/id rows with non-negative unique IDs;
    //                transducer models (requireBlankToken) must also carry sherpa's blank
    //                symbol, which attention encoder-decoder models (Canary) do not use.
    // These are structural gates, not authenticity checks; upstream publishes no hashes.
    private static void VerifyModelArtifact(string path, string fileName, bool requireBlankToken)
    {
        if (fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            VerifyOnnxProtobuf(path, fileName);
            return;
        }

        // ReSharper disable once InvertIf -- last arm of a match-then-return dispatch chain; inverting would break its shape.
        if (string.Equals(fileName, "tokens.txt", StringComparison.OrdinalIgnoreCase))
        {
            VerifyTokensFile(path, fileName, requireBlankToken);
            return;
        }

        throw new InvalidDataException(
            $"No structural verification is defined for model artifact '{fileName}'."
        );
    }

    private static void VerifyOnnxProtobuf(string path, string fileName)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
            throw new InvalidDataException($"Model artifact '{fileName}' is empty.");

        var hasPositiveIrVersion = false;
        var hasNonEmptyGraph = false;

        while (stream.Position < stream.Length)
        {
            var key = ReadProtobufVarint(stream, fileName);
            var fieldNumber = key >> 3;
            var wireType = key & 7;
            if (fieldNumber == 0)
                throw new InvalidDataException(
                    $"Model artifact '{fileName}' has an invalid protobuf field number."
                );

            switch (wireType)
            {
                case 0:
                {
                    var value = ReadProtobufVarint(stream, fileName);
                    if (fieldNumber == 1 && value > 0)
                        hasPositiveIrVersion = true;
                    break;
                }
                case 1:
                    SkipProtobufBytes(stream, 8, fileName);
                    break;
                case 2:
                {
                    var length = ReadProtobufVarint(stream, fileName);
                    if (fieldNumber == 7 && length > 0)
                        hasNonEmptyGraph = true;
                    SkipProtobufBytes(stream, length, fileName);
                    break;
                }
                case 5:
                    SkipProtobufBytes(stream, 4, fileName);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Model artifact '{fileName}' uses an invalid top-level protobuf wire type."
                    );
            }
        }

        if (!hasPositiveIrVersion || !hasNonEmptyGraph)
            throw new InvalidDataException(
                $"Model artifact '{fileName}' is not a structurally valid ONNX ModelProto."
            );
    }

    private static ulong ReadProtobufVarint(Stream stream, string fileName)
    {
        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
                throw new InvalidDataException(
                    $"Model artifact '{fileName}' ends inside a protobuf varint."
                );

            if (i == 9 && (next & 0xfe) != 0)
                throw new InvalidDataException(
                    $"Model artifact '{fileName}' contains an oversized protobuf varint."
                );

            value |= (ulong)(next & 0x7f) << (i * 7);
            if ((next & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException(
            $"Model artifact '{fileName}' contains an unterminated protobuf varint."
        );
    }

    private static void SkipProtobufBytes(FileStream stream, ulong count, string fileName)
    {
        var remaining = stream.Length - stream.Position;
        if (count > (ulong)remaining)
            throw new InvalidDataException(
                $"Model artifact '{fileName}' ends before its declared protobuf field length."
            );

        stream.Position += (long)count;
    }

    private static void VerifyTokensFile(string path, string fileName, bool requireBlankToken)
    {
        if (new FileInfo(path).Length == 0)
            throw new InvalidDataException($"Model artifact '{fileName}' is empty.");

        var ids = new HashSet<int>();
        var rowCount = 0;
        var hasBlankSymbol = false;
        try
        {
            using var reader = new StreamReader(
                path,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true
            );

            while (reader.ReadLine() is { } line)
            {
                if (line.Contains('\0'))
                    throw new InvalidDataException(
                        $"Model artifact '{fileName}' contains a null character."
                    );

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var columns = line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries
                );
                if (
                    columns.Length != 2
                    || !int.TryParse(
                        columns[^1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var id
                    )
                    || id < 0
                    || !ids.Add(id)
                )
                    throw new InvalidDataException(
                        $"Model artifact '{fileName}' has an invalid token/id row."
                    );

                hasBlankSymbol |= columns[0] is "<blk>" or "<eps>" or "<blank>";
                rowCount++;
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Model artifact '{fileName}' is not valid UTF-8.",
                ex
            );
        }

        if (rowCount == 0)
            throw new InvalidDataException(
                $"Model artifact '{fileName}' contains no token/id rows."
            );

        if (requireBlankToken && !hasBlankSymbol)
            throw new InvalidDataException(
                $"Model artifact '{fileName}' does not contain a required blank token."
            );
    }

    private static bool IsArtifactInvalidLoadFailure(Exception exception)
    {
        // ReSharper disable once SuggestVarOrType_SimpleTypes -- `var` would infer non-nullable Exception and warn on the InnerException assignment.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- type-pattern guards; the second arm's multi-line Any() would make an unwieldy `when` clause.
            if (current is InvalidDataException)
                return true;

            if (
                current is InvalidOperationException
                && s_invalidModelLoadMessageFragments.Any(
                    fragment => current.Message.Contains(
                        fragment,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
                return true;
        }

        return false;
    }

    private void DeleteInvalidModelArtifacts(
        ModelDefinition model,
        string modelDir,
        Exception failure
    )
    {
        var deleteFailures = new List<string>();
        foreach (var file in model.Files)
        {
            var path = Path.Join(modelDir, file.FileName);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                deleteFailures.Add($"{file.FileName}: {ex.Message}");
            }
        }

        _host?.Log(
            PluginLogLevel.Warning,
            $"sherpa-onnx rejected model '{model.Id}' as invalid ({failure.Message}); "
                + "deleted its artifacts so it can be downloaded again."
        );
        if (deleteFailures.Count > 0)
            _host?.Log(
                PluginLogLevel.Warning,
                "Some invalid model artifacts could not be deleted: "
                    + string.Join("; ", deleteFailures)
            );
    }

    private static OfflineRecognizer CreateParakeetRecognizer(string modelDir, string provider)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Join(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Join(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Join(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Join(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = provider;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private static OfflineRecognizer CreateCanaryRecognizer(
        string modelDir,
        string srcLang,
        string tgtLang,
        string provider
    )
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Canary.Encoder = Path.Join(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Canary.Decoder = Path.Join(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Canary.SrcLang = srcLang;
        config.ModelConfig.Canary.TgtLang = tgtLang;
        config.ModelConfig.Canary.UsePnc = 1;
        config.ModelConfig.Tokens = Path.Join(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = provider;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    // Status for the user's current EFFECTIVE provider (effectiveProvider: whether the
    // active recognizer runs on GPU). RequiresRestart is derived from the WIRED native
    // runtime (_loadedNativeProvider), not the effective provider: only a CPU-only-wired
    // runtime can't reach CUDA without a restart. A CUDA-wired runtime running a CPU
    // recognizer reports ActiveBackend=Cpu with no restart flag — a CUDA preference simply
    // triggers a reload. Instance (not static) so it can read the wired-runtime pin.
    private TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        string effectiveProvider,
        TranscriptionAccelerationPreference preference
    )
    {
        var active = string.Equals(effectiveProvider, "cuda", StringComparison.Ordinal)
            ? TranscriptionAccelerationBackend.NvidiaCuda
            : TranscriptionAccelerationBackend.Cpu;
        var displayText =
            active == TranscriptionAccelerationBackend.NvidiaCuda ? "Using NVIDIA CUDA" : "Using CPU";

        // The only toggle that needs a restart is reaching CUDA on a CPU-only-wired
        // runtime. CPU↔CUDA on a CUDA-wired runtime is a reload, not a restart.
        var requiresRestart =
            string.Equals(_loadedNativeProvider, "cpu", StringComparison.Ordinal)
            && preference == TranscriptionAccelerationPreference.NvidiaCuda;

        if (requiresRestart)
            return new TranscriptionAccelerationStatus(
                active,
                displayText,
                "Restart TypeWhisper to switch sherpa-onnx to NVIDIA CUDA.",
                true
            );

        return new TranscriptionAccelerationStatus(active, displayText);
    }

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(
        TranscriptionAccelerationPreference preference
    ) =>
        preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Preparing NVIDIA CUDA",
                "The GPU runtime downloads on the next model load."
            ),
            _ => new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Preparing CPU",
                "Will apply on next model load."
            ),
        };

    // The CUDA request fell back to CPU. requiresRestart is decided by the caller from the
    // WIRED runtime: if provisioning failed the runtime is CPU-only-wired and reaching CUDA
    // needs a restart, but if the CUDA ORT runtime was wired and only the recognizer fell
    // back, CUDA is reachable again by a reload — no restart (the point of M6).
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

    private void EnsureCanaryLanguage(string? language, bool translate)
    {
        if (_loadedModelDir is null)
            return;

        var srcLang = NormalizeCanaryLanguage(language);
        var tgtLang = translate ? "en" : srcLang;

        if (srcLang == _canarySrcLang && tgtLang == _canaryTgtLang)
            return;

        // Canary bakes src/tgt language into the recognizer config, so a language or
        // translation change requires recreating the recognizer. Reuse the EFFECTIVE
        // provider the recognizer actually runs on (_computeBackend), NOT the wired
        // runtime (_loadedNativeProvider): a CUDA-wired runtime whose recognizer fell back
        // to CPU must rebuild on CPU, or this would re-trigger the same CUDA failure.
        _recognizer?.Dispose();
        _recognizer = CreateCanaryRecognizer(
            _loadedModelDir,
            srcLang,
            tgtLang,
            _computeBackend
        );
        _canarySrcLang = srcLang;
        _canaryTgtLang = tgtLang;
    }

    internal static string NormalizeCanaryLanguage(string? language)
    {
        var normalized = language?.Trim();
        if (
            string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new NotSupportedException(
                "Sherpa ONNX Canary requires an explicit source language from the supported set: en, de, fr, es."
            );
        }

        normalized = normalized.ToLowerInvariant();
        if (!s_canarySupportedLanguages.Contains(normalized))
        {
            throw new NotSupportedException(
                "Sherpa ONNX Canary requires an explicit source language from the supported set: en, de, fr, es."
            );
        }

        return normalized;
    }

    private static float[] DecodeWav(byte[] wavData)
    {
        if (wavData.Length < 44)
            throw new ArgumentException("Invalid WAV data: too short");

        var pos = 12; // skip the leading RIFF/WAVE header
        while (pos + 8 < wavData.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavData, pos, 4);
            var chunkSize = BitConverter.ToInt32(wavData, pos + 4);

            // chunkSize comes from untrusted WAV bytes — reject anything
            // negative or larger than the remaining buffer before we use it
            // for allocation or indexing.
            if (chunkSize < 0 || chunkSize > wavData.Length - (pos + 8))
                throw new ArgumentException("Invalid WAV data: chunk size out of range");

            if (chunkId == "data")
            {
                var dataStart = pos + 8;
                // Clamp to actual buffer length so a header that lies about
                // chunk size (truncated download, malformed file) can't lead
                // to an over-read.
                var usableBytes = Math.Min(chunkSize, wavData.Length - dataStart);
                var sampleCount = usableBytes / 2; // 16-bit samples
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = BitConverter.ToInt16(wavData, dataStart + i * 2);
                    samples[i] = sample / 32768f;
                }
                return samples;
            }

            pos += 8 + chunkSize;
            // RIFF chunks are 2-byte aligned; odd-sized chunks have a pad byte.
            if (chunkSize % 2 != 0 && pos < wavData.Length)
                pos++;
        }

        throw new ArgumentException("Invalid WAV data: no data chunk found");
    }

    /// <summary>
    ///     One-shot migration from the pre-plugin layout
    ///     (<c>%LocalAppData%/TypeWhisper/Models/</c>) into the per-plugin data
    ///     directory. Best-effort: failures are logged and a stale source
    ///     directory is left alone rather than blocking activation.
    /// </summary>
    private void MigrateModelFiles()
    {
        if (_host is null)
            return;

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        var oldModelsDir = Path.Join(localAppData, "TypeWhisper", "Models");

        if (!Directory.Exists(oldModelsDir))
            return;

        foreach (var model in s_models)
        {
            var oldDir = Path.Join(oldModelsDir, model.Id);
            if (!Directory.Exists(oldDir))
                continue;

            var newDir = GetModelDirectory(model.Id);
            if (
                Directory.Exists(newDir)
                && model.Files.All(f => File.Exists(Path.Join(newDir, f.FileName)))
            )
                continue; // Already migrated

            Directory.CreateDirectory(newDir);

            foreach (var file in model.Files)
            {
                var oldPath = Path.Join(oldDir, file.FileName);
                var newPath = Path.Join(newDir, file.FileName);

                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    try
                    {
                        File.Move(oldPath, newPath);
                        Debug.WriteLine($"[SherpaOnnx] Migrated {file.FileName} for {model.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[SherpaOnnx] Failed to migrate {file.FileName}: {ex.Message}"
                        );
                    }
                }
            }

            // Clean up old directory if empty
            try
            {
                if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                    Directory.Delete(oldDir);
            }
            catch
            { /* ignore */
            }
        }
    }

    private sealed record ModelDefinition(
        string Id,
        string DisplayName,
        string SizeDescription,
        // ReSharper disable once InconsistentNaming -- MB (megabyte) is the correct unit; the suggested Mb means megabit.
        int EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended,
        bool SupportsTranslation,
        // Transducer/CTC models (Parakeet) carry a blank token in tokens.txt and sherpa's
        // native reader requires it; attention encoder-decoder models (Canary) do not, so
        // the token verifier must only demand a blank symbol when this is set.
        bool RequiresBlankToken,
        IReadOnlyList<ModelFileDefinition> Files
    );

    private sealed record ModelFileDefinition(
        string FileName,
        string DownloadUrl,
        // ReSharper disable once InconsistentNaming -- MB (megabyte) is the correct unit; the suggested Mb means megabit.
        int EstimatedSizeMB
    );
}
