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
    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private OfflineRecognizer? _recognizer;
    private SherpaCudaRuntimeInstaller? _cudaRuntimeInstaller;
    private CudaRuntimeProvisioner? _cudaProvisioner;
    private string? _loadedModelId;
    private string? _loadedModelDir;
    private string? _selectedModelId;
    private string _computeBackend = "cpu";

    // The ORT/CUDA native provider is pinned to whichever backend loads first in
    // the process; it can't be hot-swapped, so a later mismatch requires a restart.
    private string? _loadedNativeProvider;
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

            // Once a recognizer has loaded, the native provider is pinned for the
            // process lifetime. Refuse to desync _computeBackend from it — keeping
            // them equal means the requested switch is surfaced as restart-required
            // via the acceleration status (derived from the saved preference) and a
            // reload can't silently rebuild on the wrong backend.
            if (
                _loadedNativeProvider is not null
                && !string.Equals(_loadedNativeProvider, normalized, StringComparison.Ordinal)
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
            : CreateLoadedAccelerationStatus(_loadedNativeProvider, preference);
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

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
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
                await EnsureCudaRuntimeReadyAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"sherpa-onnx CUDA runtime unavailable ({ex.Message}); falling back to CPU."
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

                    // First successful load pins the native provider for the process.
                    _loadedNativeProvider ??= activeProvider;

                    _loadedModelId = modelId;
                    _loadedModelDir = dir;
                    _selectedModelId = modelId;
                    _canarySrcLang = "en";
                    _canaryTgtLang = "en";
                    _accelerationStatus = cudaUnavailableDetail is null
                        ? CreateLoadedAccelerationStatus(activeProvider, _accelerationPreference)
                        : CreateCudaUnavailableStatus(cudaUnavailableDetail);

                    Debug.WriteLine(
                        $"[SherpaOnnx] Model {modelId} loaded from {dir} ({activeProvider})"
                    );
                }
            },
            ct
        )
            .ConfigureAwait(false);
    }

    private async Task EnsureCudaRuntimeReadyAsync(CancellationToken ct)
    {
        EnsureCudaPlatformSupported();

        var provisioner =
            _cudaProvisioner
            ?? throw new InvalidOperationException("CUDA provisioner is not initialized.");
        var installer =
            _cudaRuntimeInstaller
            ?? throw new InvalidOperationException("CUDA runtime installer is not initialized.");

        // Preload the CUDA math libraries (cudart/cublas/cufft/curand/cuDNN),
        // downloading any the host doesn't already provide.
        await provisioner
            .EnsureReadyAsync(
                CudaRuntimeProfile.OnnxRuntimeCuda,
                LogProgress("CUDA libraries"),
                ct
            )
            .ConfigureAwait(false);

        // Download + extract the sherpa-onnx GPU native build.
        await installer
            .EnsureInstalledAsync(LogProgress("sherpa-onnx GPU runtime"), ct)
            .ConfigureAwait(false);

        // Route the managed bindings to the GPU dir and preload its ORT deps.
        SherpaOnnxNativeRuntime.ConfigureCudaRuntime(installer.RuntimeDirectory);
    }

    // Logs download progress in coarse 10% steps so a first-time multi-hundred-MB
    // fetch doesn't look hung, without flooding the log.
    private IProgress<double> LogProgress(string label)
    {
        var lastBucket = -1;
        return new Progress<double>(p =>
        {
            var bucket = (int)(Math.Clamp(p, 0, 1) * 10);
            if (bucket == lastBucket)
                return;
            lastBucket = bucket;
            _host?.Log(PluginLogLevel.Info, $"{label}: {p:P0}");
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

    // Test seam: simulate the process having pinned its native ORT provider to a
    // backend, without creating a real recognizer, so the CPU↔CUDA restart-required
    // status logic can be unit-tested.
    internal void MarkNativeRuntimeLoadedForTests(string provider)
    {
        var normalized = string.Equals(provider, "cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : "cpu";
        lock (_sync)
        {
            _computeBackend = normalized;
            _loadedNativeProvider = normalized;
        }
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

    // Status for the currently-loaded provider, made restart-required when the
    // user's saved preference can't be honoured by the pinned native provider.
    // Deriving from the preference (rather than stamping a one-shot status) means a
    // later reload re-computes the same restart-required state instead of clobbering
    // it with a plain "Using X".
    private static TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        string loadedProvider,
        TranscriptionAccelerationPreference preference
    )
    {
        var loaded = string.Equals(loadedProvider, "cuda", StringComparison.Ordinal)
            ? TranscriptionAccelerationBackend.NvidiaCuda
            : TranscriptionAccelerationBackend.Cpu;
        var displayText =
            loaded == TranscriptionAccelerationBackend.NvidiaCuda ? "Using NVIDIA CUDA" : "Using CPU";

        var requested = preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda =>
                TranscriptionAccelerationBackend.NvidiaCuda,
            TranscriptionAccelerationPreference.Cpu => TranscriptionAccelerationBackend.Cpu,
            // Auto (not normally seen by plugins) imposes no mismatch.
            _ => loaded,
        };

        if (requested != loaded)
        {
            var target = requested == TranscriptionAccelerationBackend.NvidiaCuda
                ? "NVIDIA CUDA"
                : "CPU";
            return new TranscriptionAccelerationStatus(
                loaded,
                displayText,
                $"Restart TypeWhisper to switch sherpa-onnx to {target}.",
                true
            );
        }

        return new TranscriptionAccelerationStatus(loaded, displayText);
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

    // The CUDA request fell back to CPU, but the native ORT provider is now pinned to
    // CPU for the process; retrying CUDA needs a restart. Flag it so the status stays
    // consistent with the requested-vs-loaded mismatch path (which a later reload
    // would otherwise re-derive as RequiresRestart=true).
    private static TranscriptionAccelerationStatus CreateCudaUnavailableStatus(string detail) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "Using CPU",
            $"CUDA unavailable: {detail}",
            RequiresRestart: true
        );

    private void EnsureCanaryLanguage(string? language, bool translate)
    {
        if (_loadedModelDir is null)
            return;

        var srcLang = NormalizeCanaryLanguage(language);
        var tgtLang = translate ? "en" : srcLang;

        if (srcLang == _canarySrcLang && tgtLang == _canaryTgtLang)
            return;

        // Canary bakes src/tgt language into the recognizer config, so a
        // language or translation change requires recreating the recognizer.
        // Reuse the provider the model was loaded with (the native provider is
        // pinned for the process anyway).
        _recognizer?.Dispose();
        _recognizer = CreateCanaryRecognizer(
            _loadedModelDir,
            srcLang,
            tgtLang,
            _loadedNativeProvider ?? "cpu"
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
