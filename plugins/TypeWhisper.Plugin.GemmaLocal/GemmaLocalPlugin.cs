using System.Diagnostics;
using System.IO;
using System.Net.Http;
using LLama;
using LLama.Common;
using LLama.Sampling;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GemmaLocal;

public sealed class GemmaLocalPlugin : ILlmProviderPlugin, IPluginSettingsProvider
{
    private static readonly IReadOnlyList<GemmaModelDefinition> Models =
    [
        new(
            "gemma4-4b-q4",
            "Gemma 4 4B (Q4_K_M)",
            "~3 GB",
            3000,
            true,
            "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf",
            "gemma-3-4b-it-Q4_K_M.gguf"
        ),
        new(
            "gemma4-12b-q4",
            "Gemma 4 12B (Q4_K_M)",
            "~8 GB",
            8000,
            false,
            "https://huggingface.co/unsloth/gemma-3-12b-it-GGUF/resolve/main/gemma-3-12b-it-Q4_K_M.gguf",
            "gemma-3-12b-it-Q4_K_M.gguf"
        ),
        new(
            "gemma4-27b-q4",
            "Gemma 4 27B (Q4_K_M)",
            "~17 GB",
            17000,
            false,
            "https://huggingface.co/unsloth/gemma-3-27b-it-GGUF/resolve/main/gemma-3-27b-it-Q4_K_M.gguf",
            "gemma-3-27b-it-Q4_K_M.gguf"
        ),
    ];

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _selectedModelId;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedModelId;
    private CancellationTokenSource? _startupCts;
    private Task? _startupTask;

    public string PluginId => "com.typewhisper.gemma-local";
    public string PluginName => "Gemma 4 (Local)";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _selectedModelId = host.GetSetting<string>("selectedModel");
        host.Log(PluginLogLevel.Info, $"Activated (model={_selectedModelId})");

        // Auto-load previously selected model in background (don't block app startup).
        // Track the task + CTS so DeactivateAsync can cancel and await it instead of
        // letting it race back to life and recreate _weights/_context after teardown.
        if (!string.IsNullOrEmpty(_selectedModelId) && IsModelDownloaded(_selectedModelId))
        {
            var modelId = _selectedModelId;
            _startupCts = new CancellationTokenSource();
            var startupCt = _startupCts.Token;
            _startupTask = Task.Run(async () =>
            {
                try
                {
                    await LoadModelAsync(modelId, startupCt);
                    host.Log(PluginLogLevel.Info, $"Auto-loaded model: {modelId}");
                }
                catch (OperationCanceledException)
                {
                    // Deactivated before startup load completed; nothing to log.
                }
                catch (Exception ex)
                {
                    host.Log(PluginLogLevel.Warning, $"Failed to auto-load model: {ex.Message}");
                }
            }, startupCt);
        }

        return Task.CompletedTask;
    }

    public async Task DeactivateAsync()
    {
        // Cancel and wait for the background startup task before tearing down
        // _context/_weights, so it can't recreate them after we unload.
        var startupCts = _startupCts;
        var startupTask = _startupTask;
        _startupCts = null;
        _startupTask = null;

        if (startupCts is not null)
        {
            try
            {
                startupCts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        if (startupTask is not null)
        {
            try
            {
                await startupTask.ConfigureAwait(false);
            }
            catch
            {
                // Startup-task exceptions are already logged via its own catch.
            }
        }

        startupCts?.Dispose();

        // Acquire _inferenceLock so we can't dispose _context/_weights while
        // ProcessAsync is mid-inference. Mirrors the unload path in
        // SetSettingValueAsync and LoadModelAsync.
        await _inferenceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            UnloadModel();
        }
        finally
        {
            _inferenceLock.Release();
        }
        _host = null;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "selectedModel",
                Label: "Model",
                Description: "Local Gemma model used for LLM processing. "
                    + "Selecting a model downloads it (if needed) and loads it; "
                    + "downloads can be several gigabytes and progress is reported to the plugin log.",
                Options: Models
                    .Select(m => new PluginSettingOption(
                        m.Id,
                        $"{m.DisplayName} ({m.SizeDescription})"
                    ))
                    .ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "selectedModel" ? _selectedModelId : null);

    public async Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        if (key != "selectedModel")
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            // Hold _inferenceLock while disposing so an in-flight ProcessAsync
            // can't be using _context/_weights when we tear them down. The
            // load path below skips the lock here because EnsureModelReadyAsync
            // may run a multi-gigabyte download — we acquire the lock inside
            // LoadModelAsync instead, only around the actual state swap.
            await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _selectedModelId = null;
                _host?.SetSetting("selectedModel", string.Empty);
                UnloadModel();
            }
            finally
            {
                _inferenceLock.Release();
            }
            _host?.NotifyCapabilitiesChanged();
            return;
        }

        SelectModel(value);
        await EnsureModelReadyAsync(value, ct);
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_selectedModelId))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, "Select a model first.")
            );

        return Task.FromResult<PluginSettingsValidationResult?>(
            _loadedModelId == _selectedModelId
                ? new PluginSettingsValidationResult(true, "Model loaded and ready.")
                : new PluginSettingsValidationResult(false, "Model selected but not loaded yet.")
        );
    }

    /// <summary>
    /// Lazily downloads (if missing) and loads the given model. Progress is
    /// reported to the plugin log since there is no progress-bar UI on Linux.
    /// </summary>
    internal async Task EnsureModelReadyAsync(string modelId, CancellationToken ct)
    {
        if (!IsModelDownloaded(modelId))
        {
            var lastPct = -1;
            var progress = new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct != lastPct && pct % 5 == 0)
                {
                    lastPct = pct;
                    Log(PluginLogLevel.Info, $"Downloading model '{modelId}': {pct}%");
                }
            });

            await DownloadModelAsync(modelId, progress, ct);
        }

        await LoadModelAsync(modelId, ct);
    }

    public string ProviderName => "Gemma 4 (Local)";
    public bool IsAvailable => _loadedModelId is not null;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
        Models
            .Select(m => new PluginModelInfo(m.Id, m.DisplayName)
            {
                SizeDescription = m.SizeDescription,
                EstimatedSizeMB = m.EstimatedSizeMB,
                IsRecommended = m.IsRecommended,
            })
            .ToList();

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        await _inferenceLock.WaitAsync(ct);
        try
        {
            if (_context is null || _weights is null)
                throw new InvalidOperationException(
                    "No model loaded. Download and load a model first."
                );

            var prompt = FormatGemmaPrompt(systemPrompt, userText);

            var executor = new StatelessExecutor(_weights, _context.Params);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 2048,
                AntiPrompts = ["<end_of_turn>", "<eos>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f },
            };

            var result = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            {
                result.Append(token);
            }

            return result.ToString().Trim();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    internal string? SelectedModelId => _selectedModelId;
    internal string? LoadedModelId => _loadedModelId;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal IReadOnlyList<GemmaModelDefinition> ModelDefinitions => Models;

    internal void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var path = GetModelFilePath(modelId, model.FileName);
        return File.Exists(path);
    }

    internal async Task DownloadModelAsync(
        string modelId,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, model.FileName);
        if (File.Exists(filePath))
        {
            progress?.Report(1.0);
            return;
        }

        Log(PluginLogLevel.Info, $"Downloading {model.DisplayName} from Hugging Face...");

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        var totalBytes =
            response.Content.Headers.ContentLength ?? model.EstimatedSizeMB * 1024L * 1024;
        long bytesRead = 0;
        var lastReport = DateTime.UtcNow;

        var buffer = new byte[81920];
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using (
            var fileStream = new FileStream(
                filePath + ".tmp",
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
                bytesRead += read;

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds > 250)
                {
                    progress?.Report((double)bytesRead / totalBytes);
                    lastReport = now;
                }
            }
        }

        File.Move(filePath + ".tmp", filePath, overwrite: true);
        progress?.Report(1.0);
        Log(PluginLogLevel.Info, $"Download complete: {model.FileName}");
    }

    internal Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var filePath = GetModelFilePath(modelId, model.FileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}");

        return Task.Run(
            async () =>
            {
                // Serialize with ProcessAsync: unloading + swapping in new weights
                // must not happen while an inference is reading _context/_weights.
                // The lock covers the full unload-then-load window so callers can't
                // observe a torn state (e.g. _weights set but _context still old).
                await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
                var loaded = false;
                try
                {
                    // If the user has switched models while we were queued behind the
                    // lock, abort: a late finish here would overwrite the newer
                    // selection and leave the wrong model loaded.
                    if (_selectedModelId is not null && _selectedModelId != modelId)
                        return;

                    UnloadModel();

                    var modelParams = new ModelParams(filePath)
                    {
                        ContextSize = 4096,
                        GpuLayerCount = 0, // CPU only (Backend.Cpu)
                        Threads = (int)Math.Max(1, Environment.ProcessorCount / 2),
                    };

                    _weights = LLamaWeights.LoadFromFile(modelParams);
                    _context = _weights.CreateContext(modelParams);
                    _loadedModelId = modelId;
                    _selectedModelId = modelId;
                    _host?.SetSetting("selectedModel", modelId);
                    loaded = true;
                }
                finally
                {
                    _inferenceLock.Release();
                }

                if (loaded)
                {
                    _host?.NotifyCapabilitiesChanged();
                    Log(PluginLogLevel.Info, $"Model loaded: {model.DisplayName}");
                }
            },
            ct
        );
    }

    internal void UnloadModel()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _loadedModelId = null;
    }

    // Helpers

    private static string FormatGemmaPrompt(string systemPrompt, string userText)
    {
        // Gemma 3 instruction-tuned chat format with proper system turn
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append("<start_of_turn>system\n");
            sb.Append(systemPrompt).Append('\n');
            sb.Append(
                "IMPORTANT: Respond ONLY in the same language as the user's input. Output ONLY the requested result, nothing else. No explanations, no extra text."
            );
            sb.Append("<end_of_turn>\n");
        }

        sb.Append("<start_of_turn>user\n");
        sb.Append(userText);
        sb.Append("<end_of_turn>\n");
        sb.Append("<start_of_turn>model\n");
        return sb.ToString();
    }

    private string GetModelDirectory(string modelId) =>
        Path.Combine(_host?.PluginDataDirectory ?? ".", "Models", modelId);

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Combine(GetModelDirectory(modelId), fileName);

    private static GemmaModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[GemmaLocal] {message}");
    }

    public void Dispose()
    {
        UnloadModel();
        _inferenceLock.Dispose();
        _httpClient.Dispose();
    }
}

internal sealed record GemmaModelDefinition(
    string Id,
    string DisplayName,
    string SizeDescription,
    int EstimatedSizeMB,
    bool IsRecommended,
    string DownloadUrl,
    string FileName
);
