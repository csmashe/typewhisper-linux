using System.Globalization;
using System.IO;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.Plugin.WhisperCpp;

public sealed class WhisperCppPlugin
    : ITypeWhisperPlugin,
        ITranscriptionEnginePlugin,
        IPluginSettingsProvider
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
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPluginHostServices? _host;
    private WhisperFactory? _factory;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private string _computeBackend = "cpu";
    private bool _runtimeLibraryOrderInitialized;
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
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => true;
    public bool SupportsModelDownload => true;
    public IReadOnlyList<string> SupportedLanguages => [];

    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];

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

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model files not found for: {modelId}", modelPath);

        await _gate.WaitAsync(ct);
        try
        {
            DisposeFactoryUnsafe();
            EnsureRuntimeLibraryOrderInitialized();
            _factory = WhisperFactory.FromPath(modelPath, CreateFactoryOptions());
            _loadedModelId = modelId;
            _selectedModelId = modelId;
            _host?.SetSetting("selectedModel", modelId);
            _accelerationStatus = CreateLoadedAccelerationStatus(
                _computeBackend,
                _accelerationPreference
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
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: NoSpeechThresholdKey,
                Label: "No-speech threshold",
                Placeholder: DefaultNoSpeechThreshold.ToString(CultureInfo.InvariantCulture),
                Description: "0.0 to 1.0. Segments whose no-speech probability exceeds this value "
                    + "are dropped so silent gaps don't get transcribed as hallucinated phrases "
                    + "(commonly \"Thank you.\"). Lower = more aggressive filtering. "
                    + "Default 0.6 matches whisper.cpp's own default. Leave blank to use the default.",
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
                    $"Using default threshold {DefaultNoSpeechThreshold.ToString(CultureInfo.InvariantCulture)}."
                )
            );

        if (
            float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0f and <= 1f
        )
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(
                    true,
                    $"Threshold set to {parsed.ToString(CultureInfo.InvariantCulture)}."
                )
            );

        return Task.FromResult<PluginSettingsValidationResult?>(
            new PluginSettingsValidationResult(
                false,
                "No-speech threshold must be a number between 0.0 and 1.0."
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
        return Path.Combine(host.PluginDataDirectory, "Models", model.FileName);
    }

    private void DisposeFactoryUnsafe()
    {
        _factory?.Dispose();
        _factory = null;
    }

    private WhisperFactoryOptions CreateFactoryOptions() =>
        new()
        {
            UseGpu = string.Equals(_computeBackend, "cuda", StringComparison.OrdinalIgnoreCase),
        };

    // RuntimeOptions.RuntimeLibraryOrder is consulted once when the native library first loads.
    // Later changes are ignored for the process lifetime, so set it once before the first factory.
    private void EnsureRuntimeLibraryOrderInitialized()
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
        _runtimeLibraryOrderInitialized = true;
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
                "Will apply on next model load."
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
