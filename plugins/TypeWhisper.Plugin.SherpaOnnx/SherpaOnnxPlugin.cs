using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using SherpaOnnx;
using TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

public sealed class SherpaOnnxPlugin : ITypeWhisperPlugin, ITranscriptionEnginePlugin
{
    private const string ParakeetRepo =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";
    private const string CanaryRepo =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";

    private static readonly IReadOnlyList<string> CanarySupportedLanguages =
    [
        "en",
        "de",
        "fr",
        "es",
    ];

    private static readonly IReadOnlyList<ModelDefinition> Models =
    [
        new(
            "parakeet-tdt-0.6b",
            "Parakeet TDT 0.6B",
            "~670 MB",
            670,
            25,
            true,
            false,
            [
                new("encoder.int8.onnx", $"{ParakeetRepo}/encoder.int8.onnx", 652),
                new("decoder.int8.onnx", $"{ParakeetRepo}/decoder.int8.onnx", 12),
                new("joiner.int8.onnx", $"{ParakeetRepo}/joiner.int8.onnx", 6),
                new("tokens.txt", $"{ParakeetRepo}/tokens.txt", 1),
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
            [
                new("encoder.int8.onnx", $"{CanaryRepo}/encoder.int8.onnx", 127),
                new("decoder.int8.onnx", $"{CanaryRepo}/decoder.int8.onnx", 71),
                new("tokens.txt", $"{CanaryRepo}/tokens.txt", 1),
            ]
        ),
    ];

    private readonly object _sync = new();

    // Drives both the model-file downloads and the on-demand CUDA runtime fetches
    // (the ~224 MB sherpa GPU tarball plus the cudart/cuBLAS/cuFFT/cuRAND/cuDNN
    // wheels, the largest of which is ~685 MB). HttpClient.Timeout bounds the WHOLE
    // request including the streamed body — even with ResponseHeadersRead — so the
    // default 100 s deadline would cancel these large fetches mid-stream on any
    // ordinary link. Use a generous ceiling (matching GemmaLocal's large-model
    // client) and rely on the per-call CancellationToken for cancellation; the
    // ceiling also bounds a stalled-but-open socket so it can't hang forever.
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private IPluginHostServices? _host;
    private OfflineRecognizer? _recognizer;
    private SherpaCudaRuntimeInstaller? _cudaRuntimeInstaller;
    private CudaRuntimeProvisioner? _cudaProvisioner;
    private string? _loadedModelId;
    private string? _loadedModelDir;
    private string? _selectedModelId;
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
    private TranscriptionAccelerationPreference _accelerationPreference =
        TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _accelerationStatus =
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    private string _canarySrcLang = "en";
    private string _canaryTgtLang = "en";

    public string PluginId => "com.typewhisper.sherpa-onnx";
    public string PluginName => "Local Models (sherpa-onnx)";
    public string PluginVersion => "1.0.0";

    public string ProviderId => "sherpa-onnx";
    public string ProviderDisplayName => "Lokal (sherpa-onnx)";
    public bool IsConfigured => true;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => _selectedModelId == "canary-180m-flash";
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

    public TranscriptionAccelerationPreference AccelerationPreference => _accelerationPreference;

    public TranscriptionAccelerationStatus AccelerationStatus => _accelerationStatus;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        Models
            .Select(m => new PluginModelInfo(m.Id, m.DisplayName)
            {
                SizeDescription = m.SizeDescription,
                EstimatedSizeMB = m.EstimatedSizeMB,
                IsRecommended = m.IsRecommended,
                LanguageCount = m.LanguageCount,
            })
            .ToList();

    public IReadOnlyList<string> SupportedLanguages =>
        _selectedModelId == "canary-180m-flash" ? CanarySupportedLanguages : [];

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;

        // Lazily provisioned on demand; the ?? lets tests inject fakes before activate.
        _cudaRuntimeInstaller ??= new SherpaCudaRuntimeInstaller(
            host.PluginAssetDirectory,
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );
        _cudaProvisioner ??= new CudaRuntimeProvisioner(
            CudaRuntimeProvisioner.DefaultCacheRoot(),
            _httpClient,
            msg => host.Log(PluginLogLevel.Info, msg)
        );

        // Register the import resolver now; until CUDA is configured it defers to
        // the default loader, which picks up the CPU runtime from the managed nuget.
        SherpaOnnxNativeRuntime.RegisterResolver();

        MigrateModelFiles();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        UnloadRecognizer();
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _selectedModelId = modelId;
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
        _accelerationPreference = preference;

        var desired = preference == TranscriptionAccelerationPreference.NvidiaCuda ? "cuda" : "cpu";

        // ConfigureComputeBackendAsync completes synchronously for SherpaOnnx (no
        // awaits in the body) and refuses to switch once the native provider is
        // pinned. Derive the status from the saved preference so a pinned mismatch
        // reads as restart-required and survives a subsequent reload (which would
        // otherwise overwrite it). The CUDA runtime is provisioned lazily on the
        // next LoadModelAsync.
        _ = ConfigureComputeBackendAsync(desired);
        _accelerationStatus = _loadedNativeProvider is null
            ? CreatePendingAccelerationStatus(preference)
            // Pass the EFFECTIVE provider (_computeBackend) for the "active backend"; the
            // restart flag is derived from the wired runtime inside the helper.
            : CreateLoadedAccelerationStatus(_computeBackend, preference);
    }

    public bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        return model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)));
    }

    public Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        _ = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);

        lock (_sync)
        {
            if (_loadedModelId == modelId)
                UnloadRecognizerUnsafe();

            if (_selectedModelId == modelId)
                _selectedModelId = null;
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

        foreach (var file in model.Files)
        {
            var filePath = Path.Combine(dir, file.FileName);
            if (File.Exists(filePath))
                continue;

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();

            var buffer = new byte[81920];
            long fileBytesRead = 0;
            var lastReport = DateTime.UtcNow;

            // Per-invocation temp name so a concurrent duplicate download can't
            // unlink an in-flight writer's file via its own catch-block cleanup.
            var tmpPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                await using (
                    var fileStream = new FileStream(
                        tmpPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        true
                    )
                )
                {
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        fileBytesRead += read;

                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds > 250 && totalBytes > 0)
                        {
                            progress?.Report(
                                (double)(cumulativeBytesRead + fileBytesRead) / totalBytes
                            );
                            lastReport = now;
                        }
                    }
                }

                File.Move(tmpPath, filePath, overwrite: true);
            }
            catch
            {
                // Cancellation or I/O failure: don't leave a partial .tmp file behind
                // to consume disk and confuse the next download attempt.
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* best effort */ }
                }
                throw;
            }

            cumulativeBytesRead += fileBytesRead;
        }

        progress?.Report(1.0);
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct) =>
        LoadModelAsync(modelId, null, ct);

    public async Task LoadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);

        if (!model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName))))
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

                    UnloadRecognizerUnsafe();

                    var activeProvider = desiredProvider;
                    try
                    {
                        _recognizer = model.SupportsTranslation
                            ? CreateCanaryRecognizer(dir, "en", "en", activeProvider)
                            : CreateParakeetRecognizer(dir, activeProvider);
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
                            : CreateParakeetRecognizer(dir, activeProvider);
                    }

                    // First successful load pins the native runtime for the process.
                    // Record the WIRED runtime (CUDA-capable vs CPU-only), not the
                    // recognizer's active provider: a CUDA-wired runtime whose recognizer
                    // fell back to CPU is still CUDA-capable, so it pins "cuda" and a later
                    // CPU↔CUDA swap needs no restart.
                    _loadedNativeProvider ??= _cudaOrtRuntimeWired ? "cuda" : activeProvider;

                    _loadedModelId = modelId;
                    _loadedModelDir = dir;
                    _selectedModelId = modelId;
                    _canarySrcLang = "en";
                    _canaryTgtLang = "en";
                    // Restart is required only if the wired runtime is CPU-only (a
                    // provisioning failure). A CUDA-wired runtime whose recognizer fell back
                    // to CPU pins "cuda" above, so CUDA is reachable again by a reload — no
                    // restart (matches CreateLoadedAccelerationStatus / the swap logic).
                    _accelerationStatus = cudaUnavailableDetail is null
                        ? CreateLoadedAccelerationStatus(activeProvider, _accelerationPreference)
                        : CreateCudaUnavailableStatus(
                            cudaUnavailableDetail,
                            requiresRestart: string.Equals(
                                _loadedNativeProvider, "cpu", StringComparison.Ordinal)
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
        // Once a recognizer has pinned the process to a non-CUDA provider — e.g. the user
        // ran on CPU and is now clicking the host's "download CUDA runtime" button — wiring
        // the GPU ORT into that live process would leave it in a mixed CPU/GPU native state.
        // Skip it: the files are now on disk, and the host's restart prompt lets a fresh
        // process wire the GPU runtime cleanly. (On the LoadModelAsync path desiredProvider
        // can only be "cuda" when the process isn't CPU-pinned, so this never blocks it.)
        bool canWireIntoProcess;
        lock (_sync)
            canWireIntoProcess =
                _loadedNativeProvider is null
                || string.Equals(_loadedNativeProvider, "cuda", StringComparison.Ordinal);

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
                var audioSamples = DecodeWav(wavAudio);
                var audioDuration = audioSamples.Length / 16000.0;

                lock (_sync)
                {
                    if (_recognizer is null || _loadedModelId is null)
                        throw new InvalidOperationException(
                            "Kein Modell geladen. LoadModelAsync zuerst aufrufen."
                        );

                    var model = GetModelDefinition(_loadedModelId);

                    if (model.SupportsTranslation)
                        EnsureCanaryLanguage(language, translate);

                    using var stream = _recognizer.CreateStream();
                    stream.AcceptWaveform(16000, audioSamples);
                    _recognizer.Decode(stream);

                    var rawText = stream.Result.Text.Trim();

                    var (text, detectedLanguage) = model.SupportsTranslation
                        ? ParseCanaryResult(rawText)
                        : (rawText, (string?)null);

                    return new PluginTranscriptionResult(
                        text,
                        detectedLanguage,
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

    private static ModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
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

    private static OfflineRecognizer CreateParakeetRecognizer(string modelDir, string provider)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
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
        config.ModelConfig.Canary.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Canary.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Canary.SrcLang = srcLang;
        config.ModelConfig.Canary.TgtLang = tgtLang;
        config.ModelConfig.Canary.UsePnc = 1;
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
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
            TranscriptionAccelerationPreference.NvidiaCuda => new(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Preparing NVIDIA CUDA",
                "The GPU runtime downloads on the next model load."
            ),
            _ => new(
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

    private static string NormalizeCanaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto")
            return "en";
        var normalized = language.Trim().ToLowerInvariant();
        return CanarySupportedLanguages.Contains(normalized) ? normalized : "en";
    }

    private static (string Text, string? DetectedLanguage) ParseCanaryResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return (string.Empty, null);

        try
        {
            using var json = JsonDocument.Parse(rawText);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return (rawText.Trim(), null);

            var text = rawText.Trim();
            if (json.RootElement.TryGetProperty("text", out var textNode))
                text = textNode.GetString()?.Trim() ?? string.Empty;

            string? lang = null;
            if (json.RootElement.TryGetProperty("lang", out var langNode))
            {
                var parsed = langNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    lang = parsed;
            }

            return (text, lang);
        }
        catch (JsonException)
        {
            return (rawText.Trim(), null);
        }
    }

    private static float[] DecodeWav(byte[] wavData)
    {
        if (wavData.Length < 44)
            throw new ArgumentException("Invalid WAV data: too short");

        var pos = 12; // skip the leading RIFF/WAVE header
        while (pos + 8 < wavData.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wavData, pos, 4);
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
        var oldModelsDir = Path.Combine(localAppData, "TypeWhisper", "Models");

        if (!Directory.Exists(oldModelsDir))
            return;

        foreach (var model in Models)
        {
            var oldDir = Path.Combine(oldModelsDir, model.Id);
            if (!Directory.Exists(oldDir))
                continue;

            var newDir = GetModelDirectory(model.Id);
            if (
                Directory.Exists(newDir)
                && model.Files.All(f => File.Exists(Path.Combine(newDir, f.FileName)))
            )
                continue; // Already migrated

            Directory.CreateDirectory(newDir);

            foreach (var file in model.Files)
            {
                var oldPath = Path.Combine(oldDir, file.FileName);
                var newPath = Path.Combine(newDir, file.FileName);

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
        int EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended,
        bool SupportsTranslation,
        IReadOnlyList<ModelFileDefinition> Files
    );

    private sealed record ModelFileDefinition(
        string FileName,
        string DownloadUrl,
        int EstimatedSizeMB
    );
}
