// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Sampling;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GemmaLocal;

public sealed class GemmaLocalPlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private static readonly IReadOnlyList<GemmaModelDefinition> s_models =
    [
        new(
            "gemma4-e2b-it-q4",
            "Gemma 4 E2B (Q4_K_M)",
            "~3 GB",
            3100,
            true,
            "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_K_M.gguf",
            "gemma-4-E2B-it-Q4_K_M.gguf"
        ),
        new(
            "gemma4-e4b-it-q4",
            "Gemma 4 E4B (Q4_K_M)",
            "~5 GB",
            5000,
            false,
            "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
            "gemma-4-E4B-it-Q4_K_M.gguf"
        ),
        new(
            "gemma4-26b-a4b-it-q4",
            "Gemma 4 26B A4B (Q4_K_M)",
            "~17 GB",
            17000,
            false,
            "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF/resolve/main/gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf"
        ),
    ];

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private IPluginHostServices? _host;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _streamResponses = true;
    private CancellationTokenSource? _startupCts;
    private Task? _startupTask;

    public string PluginId => "com.typewhisper.gemma-local";
    public string PluginName => "Gemma 4 (Local)";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        SelectedModelId = host.GetSetting<string>("selectedModel");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (model={SelectedModelId})");

        // A persisted ID may name a model that no longer exists in s_models
        // (e.g. after a release that drops a quant). IsModelDownloaded calls
        // GetModelDefinition, which throws — that would surface as a plugin
        // activation failure. Clear the stale setting instead.
        if (!string.IsNullOrEmpty(SelectedModelId)
            && s_models.All(m => m.Id != SelectedModelId))
        {
            host.Log(
                PluginLogLevel.Warning,
                $"Persisted model '{SelectedModelId}' is no longer available; clearing selection."
            );
            SelectedModelId = null;
            host.SetSetting("selectedModel", string.Empty);
        }

        // Auto-load previously selected model in background (don't block app startup).
        // Track the task + CTS so DeactivateAsync can cancel and await it instead of
        // letting it race back to life and recreate _weights/_context after teardown.
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (!string.IsNullOrEmpty(SelectedModelId) && IsModelDownloaded(SelectedModelId))
        {
            var modelId = SelectedModelId;
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
                Label: Loc.L("Settings.Model"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models
                    .Select(m => new PluginSettingOption(
                        m.Id,
                        $"{m.DisplayName} ({m.SizeDescription})"
                    ))
                    .ToList()
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "selectedModel" => SelectedModelId,
                LlmStreamingSettings.StreamResponsesSettingKey => _streamResponses ? "true" : "false",
                _ => null,
            }
        );

    public async Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        if (key == LlmStreamingSettings.StreamResponsesSettingKey)
        {
            SetStreamResponses(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            return;
        }

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
                SelectedModelId = null;
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
        if (string.IsNullOrWhiteSpace(SelectedModelId))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.SelectModel"))
            );

        return Task.FromResult<PluginSettingsValidationResult?>(
            LoadedModelId == SelectedModelId
                ? new PluginSettingsValidationResult(true, Loc.L("Settings.ModelReady"))
                : new PluginSettingsValidationResult(false, Loc.L("Settings.ModelSelectedNotLoaded"))
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
                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
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
    public bool IsAvailable => LoadedModelId is not null;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
        s_models
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

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!_streamResponses)
        {
            yield return await ProcessAsync(systemPrompt, userText, model, ct);
            yield break;
        }

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

            // InferAsync already produces tokens incrementally; yield them straight
            // through so the overlay renders the local model's output live. (The
            // batch sibling trims the accumulated result; the streamed text is not
            // trimmed — Gemma's model-turn output is normally clean.)
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            {
                yield return token;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    internal string? SelectedModelId { get; private set; }

    internal string? LoadedModelId { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    internal IReadOnlyList<GemmaModelDefinition> ModelDefinitions => s_models;

    internal void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        SelectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
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

        var filePath = Path.Join(dir, model.FileName);
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
        // Per-invocation temp name so a concurrent duplicate download can't
        // collide with an in-flight writer's FileShare.None open.
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var completed = false;
        try
        {
            await using (
                var fileStream = new FileStream(
                    tempPath,
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
                    // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                    if ((now - lastReport).TotalMilliseconds > 250)
                    {
                        progress?.Report((double)bytesRead / totalBytes);
                        lastReport = now;
                    }
                }
            }

            File.Move(tempPath, filePath, overwrite: true);
            completed = true;
        }
        finally
        {
            // A cancelled/failed download leaves a partial .tmp behind that
            // confuses the next attempt (and wastes disk on multi-GB models).
            if (!completed && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // best effort
                }
            }
        }

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
                    // If the user has switched models OR cleared the selection while we
                    // were queued behind the lock, abort: a late finish here would
                    // overwrite the newer state and load a model the user no longer wants.
                    if (SelectedModelId != modelId)
                        return;

                    UnloadModel();

                    var modelParams = new ModelParams(filePath)
                    {
                        ContextSize = 4096,
                        GpuLayerCount = 0, // CPU only (Backend.Cpu)
                        Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    };

                    // Load into a local first: if CreateContext throws, the
                    // already-loaded native weights would otherwise be stranded
                    // on the field with no owner to dispose them.
                    var newWeights = LLamaWeights.LoadFromFile(modelParams);
                    LLamaContext newContext;
                    try
                    {
                        newContext = newWeights.CreateContext(modelParams);
                    }
                    catch
                    {
                        newWeights.Dispose();
                        throw;
                    }

                    _weights = newWeights;
                    _context = newContext;

                    // The heavy load runs without the lock blocking SelectModel,
                    // so the user can switch selections while we're loading. If
                    // that happened, drop what we just loaded instead of letting
                    // the late finish silently roll back their newer choice.
                    if (SelectedModelId != modelId)
                    {
                        UnloadModel();
                        return;
                    }

                    LoadedModelId = modelId;
                    SelectedModelId = modelId;
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
        LoadedModelId = null;
    }

    // Helpers

    private static string FormatGemmaPrompt(string systemPrompt, string userText)
    {
        // Gemma instruction-tuned chat format with proper system turn
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
        Path.Join(_host?.PluginAssetDirectory ?? ".", "Models", modelId);

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Join(GetModelDirectory(modelId), fileName);

    private static GemmaModelDefinition GetModelDefinition(string modelId) =>
        s_models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[GemmaLocal] {message}");
    }

    public void Dispose()
    {
        // Cancel and await the background startup task before disposing
        // _inferenceLock/_httpClient so a late finish can't run against
        // disposed resources. Mirrors DeactivateAsync's teardown order.
        var startupCts = _startupCts;
        var startupTask = _startupTask;
        _startupCts = null;
        _startupTask = null;

        if (startupCts is not null)
        {
            try { startupCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (startupTask is not null)
        {
            try { startupTask.GetAwaiter().GetResult(); }
            catch { /* errors already logged inside the task */ }
        }

        startupCts?.Dispose();

        // Mirror DeactivateAsync: serialize teardown with any in-flight
        // ProcessAsync so we don't dispose _context/_weights mid-inference.
        _inferenceLock.Wait();
        try
        {
            UnloadModel();
        }
        finally
        {
            _inferenceLock.Release();
        }

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
