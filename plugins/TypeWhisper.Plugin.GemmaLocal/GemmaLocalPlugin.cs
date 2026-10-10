// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using TypeWhisper.Plugins.Shared.Net;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GemmaLocal;

public sealed class GemmaLocalPlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    // Downloads are pinned to one repository revision and verified against its size and SHA-256.
    // Earlier builds of the same files are accepted when already cached (see GemmaModelVerification).
    private static readonly IReadOnlyList<GemmaModelDefinition> s_models =
    [
        new(
            "gemma4-e2b-it-q4",
            "Gemma 4 E2B (Q4_K_M)",
            "~3 GB",
            3100,
            true,
            "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/0314792d7f1f7e229411f620751375812bb9faf2/gemma-4-E2B-it-Q4_K_M.gguf",
            "gemma-4-E2B-it-Q4_K_M.gguf",
            new GemmaModelFile(3106738272, "740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8"),
            [
                new GemmaModelFile(3106736256, "9378bc471710229ef165709b62e34bfb62231420ddaf6d729e727305b5b8672d"),
                new GemmaModelFile(3106735776, "ac0069ebccd39925d836f24a88c0f0c858d20578c29b21ab7cedce66ee576845"),
                new GemmaModelFile(3106731392, "f3504b387ee0962b2b041cf3691b1520118822642d67c5294f85ea62c68614b3"),
                new GemmaModelFile(3106731392, "a67d147c4b461fd5ad394acffa954ecc8686970671d2de8562d6db8888181011"),
                new GemmaModelFile(3106731136, "c8a189e581b1bf2d521792f3a1976358d737937e876c86bcdef1a979766947d6"),
            ]
        ),
        new(
            "gemma4-e4b-it-q4",
            "Gemma 4 E4B (Q4_K_M)",
            "~5 GB",
            5000,
            false,
            "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf",
            "gemma-4-E4B-it-Q4_K_M.gguf",
            new GemmaModelFile(4977171584, "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87"),
            [
                new GemmaModelFile(4977169568, "519b9793ed6ce0ff530f1b7c96e848e08e49e7af4d57bb97f76215963a54146d"),
                new GemmaModelFile(4977169088, "dff0ffba4c90b4082d70214d53ce9504a28d4d8d998276dcb3b8881a656c742a"),
                new GemmaModelFile(4977164672, "e1bc442709fe780aa4b2ec9b22c16a7fcdff542f17f01ed0e3203114d28f9f34"),
                new GemmaModelFile(4977164672, "da4f2efe4ce09d272fd6f85ce3c5ecdbf282ff841e7b2d01b69106e7d5a1d98c"),
                new GemmaModelFile(4977164416, "ced37f54b80068fe65e95c6dd79ac88cddc227e179fd1040b8f751b1e5bdf849"),
            ]
        ),
        new(
            "gemma4-26b-a4b-it-q4",
            "Gemma 4 26B A4B (Q4_K_M)",
            "~17 GB",
            17000,
            false,
            "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF/resolve/c099eb48e663fd284577b04978a94ffccb261841/gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            new GemmaModelFile(16947541728, "f2c28b3dc4776931ac6f879e11f203dec637ea0f14267a86ec8f6165f63f293f"),
            [
                new GemmaModelFile(16947539744, "34c746b1d50ab813e29cd46c4796e3f43c741901a582f93a67b55b9fc9687b35"),
            ]
        ),
    ];

    private const int ContextSize = 8192;

    // LLamaSharp does not forward cancellation into a native decode, so prefill runs in
    // small batches and a cancel waits for at most one of them.
    private const int PrefillBatchTokens = 32;

    private readonly IReadOnlyList<GemmaModelDefinition> _models;

    // The 2 h ceiling covers the whole streamed body; ConnectTimeout bounds a socket that never
    // connects and ResilientDownloader's idle watchdog bounds a stalled one.
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly Action<string, string?> _modelRoutingGuard;

    // Guards SelectedModelId only: _inferenceLock is held across the multi-second native load, so
    // it can't also serialize selection without freezing the settings UI. Never held across await.
    private readonly Lock _selectionLock = new();
    private IPluginHostServices? _host;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _streamResponses = true;
    private CancellationTokenSource? _startupCts;
    private Task? _startupTask;

    public string PluginId => "com.typewhisper.gemma-local";
    public string PluginName => "Gemma 4 (Local)";
    public string PluginVersion => PluginBuildInfo.Version;

    public GemmaLocalPlugin()
        : this(null, EnsureRequestedModelIsActive) { }

    internal GemmaLocalPlugin(
        string? loadedModelId,
        Action<string, string?> modelRoutingGuard
    )
        : this(loadedModelId, modelRoutingGuard, s_models, null) { }

    internal GemmaLocalPlugin(
        string? loadedModelId,
        Action<string, string?> modelRoutingGuard,
        IReadOnlyList<GemmaModelDefinition> models,
        HttpMessageHandler? httpHandler
    )
    {
        LoadedModelId = loadedModelId;
        _modelRoutingGuard =
            modelRoutingGuard ?? throw new ArgumentNullException(nameof(modelRoutingGuard));
        _models = models;
        _httpClient = new HttpClient(
            httpHandler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromHours(2),
        };
    }

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        SelectedModelId = host.GetSetting<string>("selectedModel");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (model={SelectedModelId})");

        // A persisted ID may name a model that no longer exists in the catalog
        // (e.g. after a release that drops a quant). IsModelDownloaded calls
        // GetModelDefinition, which throws — that would surface as a plugin
        // activation failure. Clear the stale setting instead.
        if (!string.IsNullOrEmpty(SelectedModelId)
            && _models.All(m => m.Id != SelectedModelId))
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
                // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in these teardown paths; CancelAsync() only defers callbacks, with no benefit here.
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
        // ReSharper disable once MethodSupportsCancellation -- short teardown/unload path; adding a cancellation point offers no real value.
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
                Options: _models
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
                lock (_selectionLock)
                {
                    SelectedModelId = null;
                }

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
    /// Verifies the cached model or downloads it, then loads it. Progress is
    /// reported to the plugin log since there is no progress-bar UI on Linux.
    /// </summary>
    internal async Task EnsureModelReadyAsync(string modelId, CancellationToken ct)
    {
        if (!IsModelVerified(modelId))
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

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        GetSupportedModels(_models, LoadedModelId);

    public Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    ) =>
        // Keep tokenization and native sampling off the caller's (possibly UI) context.
        Task.Run(
            async () =>
            {
                var result = new StringBuilder();
                await foreach (var piece in GenerateAsync(systemPrompt, userText, model, ct).ConfigureAwait(false))
                    result.Append(piece);
                return result.ToString().Trim();
            },
            ct
        );

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

        // The streamed text is not trimmed; Gemma's model-turn output is normally clean.
        await foreach (var piece in GenerateAsync(systemPrompt, userText, model, ct).ConfigureAwait(false))
            yield return piece;
    }

    /// <summary>
    ///     Runs one turn on the loaded context: clears its KV memory, prefills the prompt in
    ///     cancellable batches, then samples until an end-of-turn token. Serialized by
    ///     <see cref="_inferenceLock" />, so the single resident context is reused instead of
    ///     allocating a second KV cache per request.
    /// </summary>
    private async IAsyncEnumerable<string> GenerateAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _modelRoutingGuard(model, LoadedModelId);

            if (_context is not { } context || _weights is not { } weights || Markers is not { } markers)
                throw new InvalidOperationException(
                    "No model loaded. Download and load a model first."
                );

            var promptTokens = await Task.Run(() => TokenizePrompt(context, markers, systemPrompt, userText), ct)
                .ConfigureAwait(false);
            var maxTokens = LlmOutputTokenBudget.FitToContext(
                LlmOutputTokenBudget.Calculate(systemPrompt, userText),
                promptTokens.Length, checked((int)context.ContextSize), ProviderName);

            context.NativeHandle.MemoryClear();
            var batch = new LLamaBatch();
            var batchSize = Math.Min(PrefillBatchTokens, checked((int)context.BatchSize));
            for (var offset = 0; offset < promptTokens.Length; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                batch.Clear();
                var end = Math.Min(offset + batchSize, promptTokens.Length);
                for (var position = offset; position < end; position++)
                    batch.Add(promptTokens[position], position, LLamaSeqId.Zero, position == end - 1);
                await DecodeAsync(context, batch, ct).ConfigureAwait(false);
            }

            using var sampling = new DefaultSamplingPipeline { Temperature = 0.3f };
            // Special tokens decode to nothing, so turn and channel markers never reach the output.
            var decoder = new StreamingTokenDecoder(context);
            var channel = new GemmaChannelFilter(markers.ChannelStart, markers.ChannelEnd);
            var visiblePieces = 0;
            var endOfTurn = false;
            for (var generated = 0; generated < maxTokens; generated++)
            {
                ct.ThrowIfCancellationRequested();
                // Sample also accepts the token into the sampler; accepting it again would
                // count it twice in the repetition history.
                var token = sampling.Sample(context.NativeHandle, batch.TokenCount - 1);
                // Stop on the end-of-turn token itself; the same characters as text are ordinary output.
                if (token.IsEndOfGeneration(weights.Vocab) || markers.Stop.Contains(token))
                {
                    endOfTurn = true;
                    break;
                }

                if (channel.Admit((int)token))
                {
                    decoder.Add(token);
                    var piece = decoder.Read();
                    if (piece.Length > 0)
                    {
                        visiblePieces++;
                        yield return piece;
                    }
                }

                batch.Clear();
                batch.Add(token, promptTokens.Length + generated, LLamaSeqId.Zero, true);
                await DecodeAsync(context, batch, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            if (!endOfTurn)
                throw new PluginRequestException(
                    "Gemma 4 (Local) stopped the response at its output token limit.",
                    PluginRequestFailureKind.OutputTruncated,
                    isTransient: false);

            if (channel.InChannel && visiblePieces == 0)
                throw new PluginRequestException(
                    "Gemma 4 (Local) ended its turn inside a reasoning block without an answer.",
                    PluginRequestFailureKind.EmptyResponse,
                    isTransient: false);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    internal LLamaToken[] TokenizeLoadedPrompt(string systemPrompt, string userText) =>
        TokenizePrompt(
            _context ?? throw new InvalidOperationException("No model loaded."),
            Markers!,
            systemPrompt,
            userText);

    internal int[] TokenizeLoadedSpecial(string text) =>
        (_context ?? throw new InvalidOperationException("No model loaded."))
            .Tokenize(text, addBos: false, special: true)
            .Select(t => (int)t)
            .ToArray();

    internal GemmaVocabularyMarkers? Markers { get; private set; }

    // Only template segments are parsed for special tokens, so caller text cannot open or close turns.
    private static LLamaToken[] TokenizePrompt(
        LLamaContext context,
        GemmaVocabularyMarkers markers,
        string systemPrompt,
        string userText) =>
        GemmaChatFormat.Format(systemPrompt, userText)
            .SelectMany((segment, index) => segment.IsTemplate
                ? context.Tokenize(segment.Text, addBos: index == 0, special: true)
                : GemmaChatFormat.SplitLiteralMarkers(segment.Text, markers.MatchedInPlainText)
                    .SelectMany(piece => context.Tokenize(piece, addBos: false, special: false)))
            .ToArray();

    private static async Task DecodeAsync(LLamaContext context, LLamaBatch batch, CancellationToken ct)
    {
        var status = await context.DecodeAsync(batch, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (status != DecodeResult.Ok)
            throw new LLamaDecodeError(status);
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
    // ReSharper disable once ConvertToAutoPropertyWhenPossible -- expression-bodied accessor returning the shared static list; not an auto-property candidate.
    internal static IReadOnlyList<GemmaModelDefinition> ModelDefinitions => s_models;

    internal static IReadOnlyList<PluginModelInfo> GetSupportedModels(string? loadedModelId) =>
        GetSupportedModels(s_models, loadedModelId);

    private static ImmutableArray<PluginModelInfo> GetSupportedModels(
        IReadOnlyList<GemmaModelDefinition> models,
        string? loadedModelId)
    {
        if (loadedModelId is null)
            return [];

        var model = GetModelDefinition(models, loadedModelId);
        return
        [
            new PluginModelInfo(model.Id, model.DisplayName)
            {
                SizeDescription = model.SizeDescription,
                EstimatedSizeMB = model.EstimatedSizeMB,
                IsRecommended = model.IsRecommended,
            },
        ];
    }

    internal static void EnsureRequestedModelIsActive(
        string requestedModelId,
        string? activeModelId
    )
    {
        if (
            s_models.All(m =>
                !string.Equals(m.Id, requestedModelId, StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                $"Requested Gemma model '{requestedModelId}' is unknown; "
                    + $"the active Gemma model is '{activeModelId ?? "(none)"}'."
            );
        }

        if (activeModelId is null)
        {
            throw new InvalidOperationException(
                $"Requested Gemma model '{requestedModelId}' cannot run because "
                    + "the active Gemma model is '(none)'."
            );
        }

        if (!string.Equals(requestedModelId, activeModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Requested Gemma model '{requestedModelId}' does not match "
                    + $"the active Gemma model '{activeModelId}'."
            );
        }
    }

    // Test seam: free-space probe for model downloads (null = the real filesystem).
    internal Func<string, long?>? SpaceProbe { get; set; }

    internal void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        lock (_selectionLock)
        {
            SelectedModelId = modelId;
        }

        _host?.SetSetting("selectedModel", modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    /// <summary>True when a file of an accepted build's size is cached; its hash is checked on load.</summary>
    internal bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        return GemmaModelVerification.HasAcceptedSize(model, GetModelFilePath(modelId, model.FileName));
    }

    internal bool IsModelVerified(string modelId)
    {
        var model = GetModelDefinition(modelId);
        return GemmaModelVerification.IsVerified(model, GetModelFilePath(modelId, model.FileName));
    }

    /// <summary>
    ///     Verifies a cached file or downloads the pinned build, resuming a surviving
    ///     <c>.partial</c> and publishing it only once it matches the pinned hash.
    /// </summary>
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

        var spaceDescription = $"the {model.DisplayName} model";
        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The stable .partial is shared with any other app instance; serialize writers on it.
            await using var fileLock = await AcquireDownloadLockAsync(filePath, spaceDescription, ct)
                .ConfigureAwait(false);

            if (await TryUseCachedModelAsync(model, filePath, ct).ConfigureAwait(false))
            {
                progress?.Report(1.0);
                return;
            }

            if (File.Exists(filePath))
            {
                // It would never load; dropping it first keeps the space check to one copy.
                Log(PluginLogLevel.Info, $"Removing the unverified {model.DisplayName} model file.");
                GemmaModelVerification.TryDelete(GemmaModelVerification.StampPath(filePath));
                GemmaModelVerification.TryDelete(filePath);
            }

            DeleteAbandonedTempFiles(dir, model.FileName);

            // Check before any request; the downloader re-checks the server's declared remainder.
            var partialPath = filePath + ".partial";
            var partialBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            DownloadSpace.EnsureAvailable(
                dir,
                Math.Max(0, model.Pinned.SizeBytes - partialBytes),
                spaceDescription,
                SpaceProbe
            );

            Log(PluginLogLevel.Info, $"Downloading {model.DisplayName} from Hugging Face...");

            var lastReport = DateTime.MinValue;
            string? sha256 = null;
            await ResilientDownloader.DownloadToFileAsync(
                _httpClient,
                model.DownloadUrl,
                filePath,
                approxTotalBytes: model.Pinned.SizeBytes,
                idleTimeout: TimeSpan.FromSeconds(60),
                allowResume: true,
                onBytesOnDisk: onDisk =>
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds <= 250)
                        return;

                    lastReport = now;
                    progress?.Report(Math.Min(0.99, (double)onDisk / model.Pinned.SizeBytes));
                },
                verifyComplete: path => sha256 = GemmaModelVerification.VerifyDownloaded(model, path),
                ct,
                new DownloadSpaceRequirement(spaceDescription, 0, SpaceProbe)
            ).ConfigureAwait(false);

            GemmaModelVerification.WriteStamp(filePath, sha256!, GemmaModelVerification.Describe(filePath));
        }
        finally
        {
            _downloadLock.Release();
        }

        progress?.Report(1.0);
        Log(PluginLogLevel.Info, $"Download complete: {model.FileName}");
    }

    // Creating the sentinel on a full disk must surface as the space error, not a raw errno.
    private static async Task<FileStream> AcquireDownloadLockAsync(
        string filePath,
        string spaceDescription,
        CancellationToken ct)
    {
        try
        {
            return await InterProcessFileLock.AcquireAsync(filePath + ".lock", ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (DownloadSpace.TranslateWriteFailure(ex, filePath, spaceDescription) is { } noSpace)
        {
            throw noSpace;
        }
    }

    private async Task<bool> TryUseCachedModelAsync(
        GemmaModelDefinition model,
        string filePath,
        CancellationToken ct)
    {
        if (GemmaModelVerification.IsVerified(model, filePath))
            return true;

        if (!GemmaModelVerification.HasAcceptedSize(model, filePath))
            return false;

        Log(PluginLogLevel.Info, $"Verifying cached {model.DisplayName} model...");
        try
        {
            await GemmaModelVerification.VerifyCachedAsync(model, filePath, ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException ex)
        {
            Log(PluginLogLevel.Warning, $"{ex.Message} Downloading a fresh copy.");
            return false;
        }
    }

    // Earlier versions downloaded to "<file>.<guid>.tmp" and could leave one behind after a crash.
    private static void DeleteAbandonedTempFiles(string dir, string fileName)
    {
        foreach (var path in Directory.EnumerateFiles(dir, fileName + ".*.tmp"))
        {
            var guid = Path.GetFileName(path)[(fileName.Length + 1)..^".tmp".Length];
            if (Guid.TryParseExact(guid, "N", out _))
                GemmaModelVerification.TryDelete(path);
        }
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
                if (!GemmaModelVerification.IsVerified(model, filePath))
                {
                    Log(PluginLogLevel.Info, $"Verifying cached {model.DisplayName} model...");
                    await GemmaModelVerification.VerifyCachedAsync(model, filePath, ct).ConfigureAwait(false);
                }

                // Serialize with ProcessAsync: unloading + swapping in new weights
                // must not happen while an inference is reading _context/_weights.
                // The lock covers the full unload-then-load window so callers can't
                // observe a torn state (e.g. _weights set but _context still old).
                await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
                bool loaded;
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
                        ContextSize = ContextSize,
                        GpuLayerCount = 0, // CPU only (Backend.Cpu)
                        Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    };

                    // Load into a local first: if CreateContext throws, the
                    // already-loaded native weights would otherwise be stranded
                    // on the field with no owner to dispose them.
                    var newWeights = LLamaWeights.LoadFromFile(modelParams);
                    LLamaContext? newContext = null;
                    GemmaVocabularyMarkers markers;
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        newContext = newWeights.CreateContext(modelParams);
                        markers = GemmaVocabularyMarkers.Resolve(newWeights, newContext);
                    }
                    catch
                    {
                        newContext?.Dispose();
                        newWeights.Dispose();
                        throw;
                    }

                    _weights = newWeights;
                    _context = newContext;
                    Markers = markers;

                    // The heavy load runs without the lock blocking SelectModel,
                    // so the user can switch selections while we're loading. If
                    // that happened, drop what we just loaded instead of letting
                    // the late finish silently roll back their newer choice.
                    lock (_selectionLock)
                    {
                        loaded = SelectedModelId == modelId;
                        if (loaded)
                            LoadedModelId = modelId;
                    }

                    if (!loaded)
                    {
                        UnloadModel();
                        return;
                    }
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
        Markers = null;
        LoadedModelId = null;
    }

    // Helpers

    private string GetModelDirectory(string modelId) =>
        Path.Join(_host?.PluginAssetDirectory ?? ".", "Models", modelId);

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Join(GetModelDirectory(modelId), fileName);

    private GemmaModelDefinition GetModelDefinition(string modelId) =>
        GetModelDefinition(_models, modelId);

    private static GemmaModelDefinition GetModelDefinition(
        IReadOnlyList<GemmaModelDefinition> models,
        string modelId) =>
        models.FirstOrDefault(m => m.Id == modelId)
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
        // ReSharper disable once MethodSupportsCancellation -- short teardown/unload path; adding a cancellation point offers no real value.
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
        _downloadLock.Dispose();
        _httpClient.Dispose();
    }
}

internal sealed record GemmaModelDefinition(
    string Id,
    string DisplayName,
    string SizeDescription,
    // ReSharper disable once InconsistentNaming -- MB (megabyte) is the correct unit; the suggested Mb means megabit.
    int EstimatedSizeMB,
    bool IsRecommended,
    string DownloadUrl,
    string FileName,
    GemmaModelFile Pinned,
    IReadOnlyList<GemmaModelFile> EarlierBuilds
)
{
    internal IReadOnlyList<GemmaModelFile> AcceptedFiles { get; } = [Pinned, .. EarlierBuilds];
}

/// <summary>Token IDs of the Gemma 4 control markers in a loaded vocabulary.</summary>
internal sealed record GemmaVocabularyMarkers(
    IReadOnlySet<LLamaToken> Stop,
    int ChannelStart,
    int ChannelEnd,
    IReadOnlyCollection<string> MatchedInPlainText)
{
    internal static GemmaVocabularyMarkers Resolve(LLamaWeights weights, LLamaContext context)
    {
        // The prompt depends on <|turn> being one special token; a vocabulary without it is not
        // a Gemma 4 chat model and would read the template as plain text.
        if (Single(context, GemmaChatFormat.TurnStart) is null)
            throw new InvalidDataException(
                "The model vocabulary has no Gemma 4 turn marker; it is not a Gemma 4 chat model.");

        return new GemmaVocabularyMarkers(
            GemmaChatFormat.StopMarkers
                .Select(marker => Single(context, marker))
                .OfType<LLamaToken>()
                .ToHashSet(),
            (int?)Single(context, GemmaChatFormat.ChannelStart) ?? -1,
            (int?)Single(context, GemmaChatFormat.ChannelEnd) ?? -1,
            FindMarkersMatchedInPlainText(weights, context));
    }

    // User-defined control markers (Gemma 4 E2B: <|channel>, <channel|>, <|"|>) are matched by
    // the tokenizer even with special parsing off. Found once per load by scanning the vocabulary.
    private static string[] FindMarkersMatchedInPlainText(LLamaWeights weights, LLamaContext context)
    {
        var vocab = weights.Vocab;
        var markers = new List<string>();
        for (var id = 0; id < vocab.Count; id++)
        {
            var token = (LLamaToken)id;
            if ((token.GetAttributes(vocab) & (LLamaTokenAttr.Control | LLamaTokenAttr.UserDefined)) == 0)
                continue;

            var text = vocab.LLamaTokenToString(token, true);
            if (text is not null
                && GemmaChatFormat.IsControlMarker(text)
                && context.Tokenize(text, addBos: false, special: false) is [var plain]
                && plain == token)
                markers.Add(text);
        }

        return [.. markers];
    }

    private static LLamaToken? Single(LLamaContext context, string marker) =>
        context.Tokenize(marker, addBos: false, special: true) is [var token] ? token : null;
}
