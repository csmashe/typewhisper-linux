// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

public sealed class GeminiPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware,
        IModelCatalogProvider
{
    // The shared chat helper appends /v1/... to Google's compatibility base URL.
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    // Transcription, the model listing and the Files API are native-only; the compat surface
    // exposes neither the transcription models nor the interaction endpoint.
    private const string NativeBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels.v2";
    private const string SelectedLlmModelSettingName = "selectedLLMModel";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels.v1";
    private const string SelectedTranscriptionModelSettingName = "selectedTranscriptionModel";
    private const string TranscriptionModeSettingName = "transcriptionMode";
    private const string SmartModeSettingValue = "smart";
    private const string VerbatimModeSettingValue = "verbatim";
    private const int MaxVocabularyTerms = 100;
    private const int MaxVocabularyChars = 4_000;
    private const int MaxVocabularyWordsPerTerm = 8;
    private const int MaxCatalogPageCount = 100;
    // Gemini models answer with visible text plus hidden reasoning; 8192 leaves room for
    // both at low effort without asking a Flash model for more than it can return.
    private const int GeminiMaxOutputTokens = 8192;
    internal const string DefaultModel = "gemini-flash-lite-latest";
    internal const string DefaultTranscriptionModel = "gemini-3.5-transcribe";
    internal const string DefaultLiveTranscriptionModel = "gemini-3.5-transcribe-live";

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new(DefaultModel, "Gemini Flash Lite Latest") { IsRecommended = true },
        new("gemini-flash-latest", "Gemini Flash Latest"),
        new("gemini-pro-latest", "Gemini Pro Latest"),
        new("gemini-2.5-flash", "Gemini 2.5 Flash"),
        new("gemini-2.5-pro", "Gemini 2.5 Pro"),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite"),
    ];

    private static readonly IReadOnlyList<GeminiFetchedTranscriptionModel> s_fallbackTranscriptionModels =
    [
        new(DefaultTranscriptionModel, "Gemini 3.5 Transcribe", DefaultLiveTranscriptionModel),
    ];

    private static readonly string[] s_excludedModelTokens =
    [
        "embedding",
        "-image",
        "tts",
        "live",
        "audio",
        "transcribe",
        "robotics",
        "computer-use",
        "deep-research",
        "omni",
        "vision-only",
        "veo",
        "imagen",
        "aqa",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    // SelectModel is a synchronous interface member, so the transcription selection and mode
    // need a plain lock rather than the async configuration gate a catalog refresh holds.
    private readonly Lock _transcriptionSelectionSync = new();
    private long _connectionRevision;
    private List<GeminiFetchedModel> _fetchedLlmModels = [];
    private List<GeminiFetchedTranscriptionModel> _fetchedTranscriptionModels = [];
    private string? _selectedLlmModel;
    private string _selectedTranscriptionModelId = DefaultTranscriptionModel;
    private GeminiTranscriptionMode _transcriptionMode = GeminiTranscriptionMode.Smart;
    private IPluginHostServices? _host;
    private bool _streamResponses = true;

    public GeminiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
    }

    internal GeminiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.gemini";
    public string PluginName => "Google Gemini";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        // Trim on load: legacy values saved before SetApiKeyAsync trimmed would
        // otherwise reach the Bearer header with trailing whitespace and 401
        // every request while IsAvailable still reports true.
        var loaded = await host.LoadSecretAsync(ApiKeySecretName);
        ApiKey = string.IsNullOrWhiteSpace(loaded) ? null : loaded.Trim();
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        _selectedLlmModel = host.GetSetting<string>(SelectedLlmModelSettingName);
        lock (_transcriptionSelectionSync)
        {
            _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(
                host.GetSetting<List<GeminiFetchedTranscriptionModel>>(FetchedTranscriptionModelsSettingName) ?? []);
            _transcriptionMode = ParseTranscriptionMode(host.GetSetting<string>(TranscriptionModeSettingName));
            _selectedTranscriptionModelId = NormalizeSelectedTranscriptionModelId(
                host.GetSetting<string>(SelectedTranscriptionModelSettingName));
        }

        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "gemini";
    public string ProviderDisplayName => "Google Gemini";
    public bool IsConfigured => IsAvailable;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var models = AvailableTranscriptionModels;
            var defaultModelId = ResolveDefaultTranscriptionModelId(models);
            return models
                .Select(model => new PluginModelInfo(
                    model.Id,
                    model.DisplayName ?? FormatModelDisplayName(model.Id))
                {
                    IsRecommended = string.Equals(
                        model.Id,
                        defaultModelId,
                        StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }
    }

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedModelId
    {
        get
        {
            lock (_transcriptionSelectionSync)
                return _selectedTranscriptionModelId;
        }
    }

    public bool SupportsTranslation => false;

    // Without this the host drops every advisory hint and never calls the hint-aware overloads.
    public bool SupportsLanguageHints => true;

    public bool SupportsStreaming =>
        IsConfigured && SelectedTranscriptionModel?.LiveModelId is not null;

    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        lock (_transcriptionSelectionSync)
        {
            var match = AvailableTranscriptionModels.FirstOrDefault(model =>
                    string.Equals(model.Id, normalized, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown transcription model: {modelId}", nameof(modelId));
            if (string.Equals(_selectedTranscriptionModelId, match.Id, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedTranscriptionModelId = match.Id;
            _host?.SetSetting(SelectedTranscriptionModelSettingName, match.Id);
        }

        _host?.NotifyCapabilitiesChanged();
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(
            wavAudio,
            NormalizeLanguage(language) is { } normalized ? [normalized] : [],
            translate,
            prompt,
            ct);

    // Gemini has no progress callback, so the polling preview is the batch call with every hint;
    // without this override the SDK default would collapse the hints to the first one.
    public Task<PluginTranscriptionResult> TranscribeStreamingWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(wavAudio, languageHints, translate, prompt, ct);

    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Gemini does not support translation.");

        if (!IsConfigured || SelectedTranscriptionModel is not { } model)
        {
            throw new PluginRequestException(
                Loc.L("Settings.ApiKeyNotConfigured"),
                PluginRequestFailureKind.Configuration);
        }

        return await GeminiTranscriptionClient.TranscribeAsync(
            _httpClient,
            NativeBaseUrl,
            ApiKey!,
            model.Id,
            wavAudio,
            NormalizeLanguageHints(languageHints),
            ExtractVocabulary(prompt),
            TranscriptionMode,
            (level, message) => _host?.Log(level, message),
            ct);
    }

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            NormalizeLanguage(language) is { } normalized ? [normalized] : [], ct);

    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new PluginRequestException(
                Loc.L("Settings.ApiKeyNotConfigured"),
                PluginRequestFailureKind.Configuration);
        }

        if (SelectedTranscriptionModel?.LiveModelId is not { } liveModelId)
        {
            throw new NotSupportedException(
                "The selected Gemini transcription model has no live sibling.");
        }

        return await GeminiStreamingSession.ConnectAsync(
            ApiKey!,
            liveModelId,
            NormalizeLanguageHints(languageHints),
            // Live sessions carry no prompt, so there are no dictionary terms to pass on.
            customVocabulary: [],
            TranscriptionMode,
            ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "Google Gemini";
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            var models = _fetchedLlmModels;
            if (models.Count == 0)
            {
                var fallbackDefaultId = s_fallbackLlmModels.FirstOrDefault(model => string.Equals(
                    model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id ?? DefaultModel;
                return s_fallbackLlmModels
                    .OrderByDescending(model => string.Equals(
                        model.Id, fallbackDefaultId, StringComparison.OrdinalIgnoreCase))
                    .Select(model => model with
                    {
                        IsRecommended = string.Equals(model.Id, fallbackDefaultId, StringComparison.OrdinalIgnoreCase),
                    })
                    .ToList();
            }

            // Host services use the first model as their implicit default.
            var defaultModelId = models.FirstOrDefault(model => string.Equals(
                model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id ?? ResolveDefaultModelId(models);
            return models
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    defaultModelId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new PluginModelInfo(model.Id, model.DisplayName ?? model.Id)
                {
                    IsRecommended = string.Equals(model.Id, defaultModelId, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        model = ResolveRequestModel(model);
        try
        {
            return await OpenAiChatHelper.SendChatCompletionAsync(
                _httpClient,
                BaseUrl,
                ApiKey!,
                model,
                systemPrompt,
                userText,
                CreateRequestOptions(model, systemPrompt, userText),
                ct
            );
        }
        catch (JsonException ex)
        {
            throw new PluginRequestException(
                "The provider returned a malformed response.",
                PluginRequestFailureKind.EmptyResponse,
                innerException: ex);
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

        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        model = ResolveRequestModel(model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            model,
            systemPrompt,
            userText,
            CreateRequestOptions(model, systemPrompt, userText),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal IReadOnlyList<GeminiFetchedModel> FetchedLlmModels => _fetchedLlmModels;

    internal IReadOnlyList<GeminiFetchedTranscriptionModel> FetchedTranscriptionModels =>
        _fetchedTranscriptionModels;

    internal GeminiTranscriptionMode TranscriptionMode
    {
        get
        {
            lock (_transcriptionSelectionSync)
                return _transcriptionMode;
        }
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        await _configurationGate.WaitAsync();
        try
        {
            var normalized = NormalizeApiKey(apiKey);
            var previousApiKey = ApiKey;
            var wasAvailable = IsAvailable;
            var changed = !string.Equals(previousApiKey, normalized, StringComparison.Ordinal);
            var catalogChanged = changed && _fetchedLlmModels.Count > 0;
            var transcriptionCatalogChanged = changed && _fetchedTranscriptionModels.Count > 0;

            // The transcription role has no id-agnostic fallback, so a selection left pointing at
            // a fetched model would report "not configured" with a valid key. The repair is read
            // here and written with the catalog clear below so the persisted value cannot survive
            // as the stale id a later refresh would then consider unchanged.
            var previousTranscriptionSelection = string.Empty;
            var repairedTranscriptionSelection = string.Empty;
            if (transcriptionCatalogChanged)
            {
                lock (_transcriptionSelectionSync)
                {
                    previousTranscriptionSelection = _selectedTranscriptionModelId;
                    repairedTranscriptionSelection = NormalizeSelectedTranscriptionModelId(
                        _selectedTranscriptionModelId,
                        s_fallbackTranscriptionModels);
                }
            }

            var transcriptionSelectionChanged = !string.Equals(
                previousTranscriptionSelection,
                repairedTranscriptionSelection,
                StringComparison.Ordinal);

            if (_host is not null)
            {
                var secretPersisted = false;
                var catalogWriteAttempted = false;
                var transcriptionCatalogWriteAttempted = false;
                var transcriptionSelectionWriteAttempted = false;
                try
                {
                    await PersistApiKeyAsync(_host, normalized);
                    secretPersisted = true;
                    if (catalogChanged)
                    {
                        catalogWriteAttempted = true;
                        _host.SetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName, []);
                    }

                    if (transcriptionCatalogChanged)
                    {
                        transcriptionCatalogWriteAttempted = true;
                        _host.SetSetting<List<GeminiFetchedTranscriptionModel>>(
                            FetchedTranscriptionModelsSettingName, []);
                    }

                    if (transcriptionSelectionChanged)
                    {
                        transcriptionSelectionWriteAttempted = true;
                        _host.SetSetting(
                            SelectedTranscriptionModelSettingName,
                            repairedTranscriptionSelection);
                    }
                }
                catch (Exception writeException)
                {
                    List<Exception> rollbackFailures = [];
                    if (secretPersisted)
                    {
                        try
                        {
                            await PersistApiKeyAsync(_host, previousApiKey);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (catalogWriteAttempted)
                    {
                        try
                        {
                            _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (transcriptionCatalogWriteAttempted)
                    {
                        try
                        {
                            _host.SetSetting(
                                FetchedTranscriptionModelsSettingName,
                                _fetchedTranscriptionModels);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (transcriptionSelectionWriteAttempted)
                    {
                        try
                        {
                            _host.SetSetting(
                                SelectedTranscriptionModelSettingName,
                                previousTranscriptionSelection);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Failed to persist the API key and restore the previous state.",
                            new AggregateException([writeException, .. rollbackFailures]));
                    }

                    throw;
                }
            }

            ApiKey = normalized;
            if (changed)
                Interlocked.Increment(ref _connectionRevision);
            // The LLM selection deliberately survives a key change: the fallback aliases or the
            // next successful refresh repair it, and re-entering a key keeps the user's choice.
            if (catalogChanged)
                _fetchedLlmModels = [];
            if (transcriptionCatalogChanged)
            {
                // The repaired id read above is applied verbatim so memory and the persisted
                // setting cannot diverge.
                lock (_transcriptionSelectionSync)
                {
                    _fetchedTranscriptionModels = [];
                    _selectedTranscriptionModelId = repairedTranscriptionSelection;
                }
            }

            if (_host is not null
                && ((changed && wasAvailable != IsAvailable)
                    || catalogChanged
                    || transcriptionCatalogChanged))
            {
                _host.NotifyCapabilitiesChanged();
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal async Task<bool> SetFetchedLlmModelsAsync(
        IEnumerable<GeminiFetchedModel> models,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeFetchedLlmModels(models);
        await _configurationGate.WaitAsync(ct);
        try
        {
            if (expectedRevision is not null
                && Interlocked.Read(ref _connectionRevision) != expectedRevision)
            {
                _host?.Log(PluginLogLevel.Debug, "Discarded a model catalog fetched for a previous API key.");
                return false;
            }

            var catalogChanged = !ModelCatalogsEqual(_fetchedLlmModels, normalized);
            var selectedModel = normalized.FirstOrDefault(model => string.Equals(
                model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id
                ?? (normalized.Count > 0 ? ResolveDefaultModelId(normalized) : DefaultModel);
            var selectionChanged = !string.Equals(_selectedLlmModel, selectedModel, StringComparison.Ordinal);
            if (!catalogChanged && !selectionChanged)
                return true;

            PersistCatalogAndSelection(
                "model catalog",
                FetchedLlmModelsSettingName,
                normalized,
                _fetchedLlmModels,
                catalogChanged,
                SelectedLlmModelSettingName,
                selectedModel,
                _selectedLlmModel,
                selectionChanged);

            _fetchedLlmModels = normalized;
            _selectedLlmModel = selectedModel;
            if (catalogChanged)
                _host?.NotifyCapabilitiesChanged();
            return true;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    // Both catalogs persist the same pair -- the catalog and the selection repaired against it --
    // and restore whichever of the two was already written when the second write fails.
    private void PersistCatalogAndSelection<TModel>(
        string catalogLabel,
        string catalogSettingName,
        List<TModel> catalog,
        List<TModel> previousCatalog,
        bool catalogChanged,
        string selectionSettingName,
        string? selection,
        string? previousSelection,
        bool selectionChanged)
    {
        if (_host is null)
            return;

        var selectionWriteAttempted = false;
        try
        {
            if (catalogChanged)
                _host.SetSetting(catalogSettingName, catalog);
            // ReSharper disable once InvertIf -- an early return out of this try would hide that
            // both writes share one rollback; the positive form keeps them side by side.
            if (selectionChanged)
            {
                selectionWriteAttempted = true;
                _host.SetSetting(selectionSettingName, selection);
            }
        }
        catch (Exception writeException)
        {
            try
            {
                if (catalogChanged)
                    _host.SetSetting(catalogSettingName, previousCatalog);
                if (selectionWriteAttempted)
                    _host.SetSetting(selectionSettingName, previousSelection);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    $"Failed to persist the {catalogLabel} and restore the previous value.",
                    new AggregateException(writeException, rollbackException));
            }

            throw;
        }
    }

    // Both catalog fetches answer a failed request the same way: log it and return null so the
    // cached catalog survives untouched; only the caller's own cancellation propagates.
    private async Task<T?> TryFetchAsync<T>(
        string catalogLabel,
        Func<Task<T?>> fetch,
        CancellationToken ct)
        where T : class
    {
        try
        {
            return await fetch();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _host?.Log(PluginLogLevel.Warning, $"{catalogLabel} request timed out.");
            return null;
        }
        catch (OperationCanceledException)
        {
            _host?.Log(PluginLogLevel.Debug, $"{catalogLabel} request was canceled by the caller.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"{catalogLabel} request failed with {ex.GetType().Name}.");
            return null;
        }
        catch (JsonException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"{catalogLabel} parsing failed with {ex.GetType().Name}.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"{catalogLabel} request failed with {ex.GetType().Name}.");
            return null;
        }
    }

    private static Task PersistApiKeyAsync(IPluginHostServices host, string? apiKey) =>
        apiKey is null
            ? host.DeleteSecretAsync(ApiKeySecretName)
            : host.StoreSecretAsync(ApiKeySecretName, apiKey);

    internal async Task<List<GeminiFetchedModel>?> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        await _configurationGate.WaitAsync(ct);
        string? apiKey;
        long revision;
        try
        {
            apiKey = ApiKey;
            revision = Interlocked.Read(ref _connectionRevision);
        }
        finally
        {
            _configurationGate.Release();
        }
        if (apiKey is null)
            return null;

        return await TryFetchAsync("Model catalog", async () =>
        {
            using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Model catalog request failed with HTTP status {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var catalog = JsonSerializer.Deserialize<GeminiCompatibleModelsResponse>(json, s_jsonOptions);
            if (catalog?.Data is null)
            {
                _host?.Log(PluginLogLevel.Warning, "Model catalog response did not contain a data array.");
                return null;
            }

            // ReSharper disable once InvertIf -- guard-clause form matches the two bail-outs above it; inverting would bury the success path in an if.
            if (Interlocked.Read(ref _connectionRevision) != revision)
            {
                _host?.Log(PluginLogLevel.Debug, "Discarded a model catalog fetched for a previous API key.");
                return null;
            }

            return NormalizeFetchedLlmModels(catalog.Data.OfType<GeminiCompatibleModel>());
        }, ct);
    }

    // A second, transcription-only fetch: the OpenAI-compat listing the LLM catalog uses carries
    // no baseModelId and does not list transcription models at all.
    internal async Task<List<GeminiFetchedTranscriptionModel>?> FetchTranscriptionModelsAsync(
        CancellationToken ct = default)
    {
        await _configurationGate.WaitAsync(ct);
        string? apiKey;
        long revision;
        try
        {
            apiKey = ApiKey;
            revision = Interlocked.Read(ref _connectionRevision);
        }
        finally
        {
            _configurationGate.Release();
        }
        if (apiKey is null)
            return null;

        return await TryFetchAsync("Transcription model catalog", async () =>
        {
            List<GeminiNativeModel> nativeModels = [];
            HashSet<string> seenPageTokens = new(StringComparer.Ordinal);
            string? pageToken = null;
            var pageCount = 0;
            do
            {
                pageCount++;
                var url = $"{NativeBaseUrl}/models?pageSize=1000";
                if (!string.IsNullOrWhiteSpace(pageToken))
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                using var request = CreateNativeRequest(HttpMethod.Get, url, apiKey);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Transcription model catalog request failed with HTTP status {(int)response.StatusCode}.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<GeminiNativeModelsResponse>(json, s_jsonOptions);
                if (page?.Models is null)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        "Transcription model catalog response did not contain a models array.");
                    return null;
                }

                nativeModels.AddRange(page.Models.OfType<GeminiNativeModel>());
                var nextPageToken = string.IsNullOrWhiteSpace(page.NextPageToken)
                    ? null
                    : page.NextPageToken;
                if (nextPageToken is not null && !seenPageTokens.Add(nextPageToken))
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        "Transcription model catalog pagination returned a repeated page token.");
                    nextPageToken = null;
                }

                pageToken = nextPageToken;
            }
            while (pageToken is not null && pageCount < MaxCatalogPageCount);

            if (pageToken is not null)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Transcription model catalog pagination exceeded {MaxCatalogPageCount} pages.");
            }

            // ReSharper disable once InvertIf -- guard-clause form matches the bail-outs above it.
            if (Interlocked.Read(ref _connectionRevision) != revision)
            {
                _host?.Log(
                    PluginLogLevel.Debug,
                    "Discarded a transcription model catalog fetched for a previous API key.");
                return null;
            }

            return NormalizeFetchedTranscriptionModels(nativeModels);
        }, ct);
    }

    internal async Task<bool> SetFetchedTranscriptionModelsAsync(
        IEnumerable<GeminiFetchedTranscriptionModel> models,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeFetchedTranscriptionModels(models);
        await _configurationGate.WaitAsync(ct);
        try
        {
            if (expectedRevision is not null
                && Interlocked.Read(ref _connectionRevision) != expectedRevision)
            {
                _host?.Log(
                    PluginLogLevel.Debug,
                    "Discarded a transcription model catalog fetched for a previous API key.");
                return false;
            }

            // The selection read, its repair and both writes stay under the same lock a
            // synchronous SelectModel takes, so a dropdown pick cannot land between them.
            lock (_transcriptionSelectionSync)
            {
                var catalogChanged = !TranscriptionModelCatalogsEqual(_fetchedTranscriptionModels, normalized);
                var selectedModel = NormalizeSelectedTranscriptionModelId(
                    _selectedTranscriptionModelId,
                    normalized.Count > 0 ? normalized : s_fallbackTranscriptionModels);
                var selectionChanged = !string.Equals(
                    _selectedTranscriptionModelId,
                    selectedModel,
                    StringComparison.Ordinal);
                if (!catalogChanged && !selectionChanged)
                    return true;

                PersistCatalogAndSelection(
                    "transcription model catalog",
                    FetchedTranscriptionModelsSettingName,
                    normalized,
                    _fetchedTranscriptionModels,
                    catalogChanged,
                    SelectedTranscriptionModelSettingName,
                    selectedModel,
                    _selectedTranscriptionModelId,
                    selectionChanged);

                _fetchedTranscriptionModels = normalized;
                _selectedTranscriptionModelId = selectedModel;
            }

            _host?.NotifyCapabilitiesChanged();
            return true;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", normalized);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsCompatibleChatModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        if (!normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !s_excludedModelTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    // The native endpoints reject Bearer authentication and want the key in this header.
    internal static HttpRequestMessage CreateNativeRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        return request;
    }

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiCompatibleModel> models) =>
        NormalizeFetchedLlmModels(models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                model.Id!,
                model.DisplayName)));

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiFetchedModel> models) =>
        models
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- cached catalogs come from JSON settings, where a null array entry deserializes to a null element despite the non-nullable annotation.
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim()))
            .Where(model => IsCompatibleChatModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolveDefaultModelId(List<GeminiFetchedModel> models)
    {
        var alias = models.FirstOrDefault(model => string.Equals(
            model.Id,
            DefaultModel,
            StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
            return alias.Id;

        return models
            .Where(model => model.Id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase)
                || model.Id.Contains("experimental", StringComparison.OrdinalIgnoreCase)
                || model.Id.Contains("-exp", StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? 0
                : model.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 1
                : model.Id.Contains("pro", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? models[0].Id;
    }

    private static Version GetModelVersion(string id)
    {
        const string prefix = "gemini-";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new Version(0, 0);

        var versionEnd = id.IndexOf('-', prefix.Length);
        if (versionEnd <= prefix.Length)
            return new Version(0, 0);

        var versionText = id[prefix.Length..versionEnd];
        if (!versionText.Contains('.'))
            versionText += ".0";

        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(0, 0);
    }

    private static bool CatalogsEqual<TModel>(
        List<TModel> first,
        List<TModel> second,
        Func<TModel, TModel, bool> entriesEqual) =>
        first.Count == second.Count
        && first.Zip(second).All(pair => entriesEqual(pair.First, pair.Second));

    private static bool ModelCatalogsEqual(
        List<GeminiFetchedModel> first,
        List<GeminiFetchedModel> second) =>
        CatalogsEqual(first, second, (left, right) =>
            string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal));

    private static string NormalizeModelId(string id)
    {
        var normalized = id.Trim();
        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized["models/".Length..]
            : normalized;
    }

    internal static bool IsCompatibleTranscriptionModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        return normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("-transcribe", StringComparison.OrdinalIgnoreCase)
            && !IsLiveTranscriptionModelId(normalized);
    }

    private IReadOnlyList<GeminiFetchedTranscriptionModel> AvailableTranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels
            : s_fallbackTranscriptionModels;

    // Resolves a selection that is no longer in the catalog to the default rather than to null,
    // so a configured key never reports the transcription role as unconfigured.
    private GeminiFetchedTranscriptionModel? SelectedTranscriptionModel
    {
        get
        {
            lock (_transcriptionSelectionSync)
            {
                var models = AvailableTranscriptionModels;
                var selected = NormalizeSelectedTranscriptionModelId(
                    _selectedTranscriptionModelId,
                    models);
                return models.FirstOrDefault(model => string.Equals(
                    model.Id,
                    selected,
                    StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private string NormalizeSelectedTranscriptionModelId(string? modelId) =>
        NormalizeSelectedTranscriptionModelId(modelId, AvailableTranscriptionModels);

    private static string NormalizeSelectedTranscriptionModelId(
        string? modelId,
        IReadOnlyList<GeminiFetchedTranscriptionModel> available)
    {
        var normalized = string.IsNullOrWhiteSpace(modelId) ? null : NormalizeModelId(modelId);
        return available.FirstOrDefault(model => string.Equals(
                model.Id,
                normalized,
                StringComparison.OrdinalIgnoreCase))?.Id
            ?? ResolveDefaultTranscriptionModelId(available);
    }

    // A live sibling is only claimed when the listing actually carries it, so a fetched catalog
    // never advertises streaming for a model that has no -live counterpart.
    private static List<GeminiFetchedTranscriptionModel> NormalizeFetchedTranscriptionModels(
        IEnumerable<GeminiNativeModel> models)
    {
        var available = models
            .Select(model => new GeminiFetchedModel(
                NormalizeModelId(ResolveNativeModelId(model)),
                model.DisplayName))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var liveModelIds = available
            .Where(model => IsLiveTranscriptionModelId(model.Id))
            .Select(model => model.Id)
            .ToList();

        return NormalizeFetchedTranscriptionModels(available
            .Where(model => IsCompatibleTranscriptionModelId(model.Id))
            .Select(model => new GeminiFetchedTranscriptionModel(
                model.Id,
                model.DisplayName,
                ResolveLiveSibling(model.Id, liveModelIds))));
    }

    private static List<GeminiFetchedTranscriptionModel> NormalizeFetchedTranscriptionModels(
        IEnumerable<GeminiFetchedTranscriptionModel> models) =>
        models
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- cached catalogs come from JSON settings, where a null array entry deserializes to a null element despite the non-nullable annotation.
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedTranscriptionModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(model.LiveModelId)
                    ? null
                    : NormalizeModelId(model.LiveModelId)))
            .Where(model => IsCompatibleTranscriptionModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? ResolveLiveSibling(
        string transcriptionModelId,
        IReadOnlyList<string> liveModelIds) =>
        liveModelIds.FirstOrDefault(liveModelId => string.Equals(
            liveModelId,
            transcriptionModelId + "-live",
            StringComparison.OrdinalIgnoreCase));

    private static string ResolveDefaultTranscriptionModelId(
        IReadOnlyList<GeminiFetchedTranscriptionModel> models) =>
        models
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? DefaultTranscriptionModel;

    private static bool TranscriptionModelCatalogsEqual(
        List<GeminiFetchedTranscriptionModel> first,
        List<GeminiFetchedTranscriptionModel> second) =>
        CatalogsEqual(first, second, (left, right) =>
            string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && string.Equals(left.LiveModelId, right.LiveModelId, StringComparison.OrdinalIgnoreCase));

    private static string ResolveNativeModelId(GeminiNativeModel model) =>
        !string.IsNullOrWhiteSpace(model.BaseModelId)
            ? model.BaseModelId
            : model.Name ?? "";

    private static bool IsLiveTranscriptionModelId(string id) =>
        NormalizeModelId(id).Contains("-transcribe-live", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalized;
    }

    private static List<string> NormalizeLanguageHints(IReadOnlyList<string> languageHints)
    {
        List<string> normalized = [];
        foreach (var languageHint in languageHints)
        {
            if (NormalizeLanguage(languageHint) is not { } value
                || normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized.Add(value);
        }

        return normalized;
    }

    // Only the HTTP API sends a prompt; dictation passes none, so this is usually empty.
    internal static IReadOnlyList<string> ExtractVocabulary(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> terms = [];
        var totalChars = 0;
        foreach (var rawTerm in prompt.Split(
            [',', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(rawTerm) || CountWords(rawTerm) > MaxVocabularyWordsPerTerm)
                continue;

            // The separators count toward the provider's total-length budget.
            var nextTotal = totalChars + (terms.Count == 0 ? 0 : 2) + rawTerm.Length;
            if (nextTotal > MaxVocabularyChars)
                break;

            terms.Add(rawTerm);
            totalChars = nextTotal;
            if (terms.Count == MaxVocabularyTerms)
                break;
        }

        return terms;
    }

    // The HTTP API merges the caller's free-form prompt into the dictionary terms, and a
    // sentence is a biasing instruction, not a vocabulary entry.
    private static int CountWords(string term) =>
        term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string FormatModelDisplayName(string modelId) =>
        string.Join(' ', NormalizeModelId(modelId)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static GeminiTranscriptionMode ParseTranscriptionMode(string? mode) =>
        string.Equals(mode, VerbatimModeSettingValue, StringComparison.OrdinalIgnoreCase)
            ? GeminiTranscriptionMode.Verbatim
            : GeminiTranscriptionMode.Smart;

    private static string FormatTranscriptionMode(GeminiTranscriptionMode mode) =>
        mode == GeminiTranscriptionMode.Verbatim
            ? VerbatimModeSettingValue
            : SmartModeSettingValue;

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    public void Dispose()
    {
        _configurationGate.Dispose();
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "AIza...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: _fetchedLlmModels.Count > 0
                    ? Loc.L("Settings.LlmModelDescriptionFetched", _fetchedLlmModels.Count)
                    : Loc.L("Settings.LlmModelDescriptionDefault"),
                Options: SupportedModels.Select(model => new PluginSettingOption(model.Id, model.DisplayName)).ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: SelectedTranscriptionModelSettingName,
                Label: Loc.L("Settings.TranscriptionModel"),
                Description: _fetchedTranscriptionModels.Count > 0
                    ? Loc.L("Settings.TranscriptionModelsFetched", _fetchedTranscriptionModels.Count)
                    : Loc.L("Settings.TranscriptionModelFallback"),
                Options: TranscriptionModels
                    .Select(model => new PluginSettingOption(model.Id, model.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TranscriptionModeSettingName,
                Label: Loc.L("Settings.TranscriptionMode"),
                Description: Loc.L("Settings.TranscriptionModeHint"),
                Options:
                [
                    new PluginSettingOption(SmartModeSettingValue, Loc.L("Settings.ModeSmart")),
                    new PluginSettingOption(VerbatimModeSettingValue, Loc.L("Settings.ModeVerbatim")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                ApiKeySecretName => ApiKey,
                SelectedLlmModelSettingName => _selectedLlmModel ?? SupportedModels[0].Id,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                SelectedTranscriptionModelSettingName => SelectedModelId,
                TranscriptionModeSettingName => FormatTranscriptionMode(TranscriptionMode),
                _ => null,
            }
        );

    public async Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case SelectedLlmModelSettingName:
                await SelectLlmModelAsync(value, ct);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
            case SelectedTranscriptionModelSettingName:
                SelectTranscriptionModelFromSettings(value);
                break;
            case TranscriptionModeSettingName:
                SetTranscriptionMode(ParseTranscriptionMode(value));
                break;
        }
    }

    // Unlike SelectModel this repairs an unknown id instead of throwing: a stale saved value
    // must not block the settings pane from saving.
    private void SelectTranscriptionModelFromSettings(string? value)
    {
        lock (_transcriptionSelectionSync)
        {
            var repaired = NormalizeSelectedTranscriptionModelId(value);
            if (string.Equals(_selectedTranscriptionModelId, repaired, StringComparison.Ordinal))
                return;

            _selectedTranscriptionModelId = repaired;
            _host?.SetSetting(SelectedTranscriptionModelSettingName, repaired);
        }

        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTranscriptionMode(GeminiTranscriptionMode mode)
    {
        lock (_transcriptionSelectionSync)
        {
            if (_transcriptionMode == mode)
                return;

            _transcriptionMode = mode;
            _host?.SetSetting(TranscriptionModeSettingName, FormatTranscriptionMode(mode));
        }
    }

    // Runs under the configuration gate so a settings save cannot interleave with a
    // catalog refresh; ids outside the catalog fall back to the recommended model like
    // the OpenAI plugin does.
    private async Task SelectLlmModelAsync(string? value, CancellationToken ct)
    {
        await _configurationGate.WaitAsync(ct);
        try
        {
            var requested = string.IsNullOrWhiteSpace(value) ? null : NormalizeModelId(value);
            var modelId = requested is null
                ? null
                : SupportedModels.FirstOrDefault(model =>
                        string.Equals(model.Id, requested, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? SupportedModels[0].Id;
            _host?.SetSetting(SelectedLlmModelSettingName, modelId);
            _selectedLlmModel = modelId;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // IModelCatalogProvider contract: a failed or empty fetch must leave the cached
    // catalog and the selection untouched; only a non-empty response replaces them.
    public async Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        await RefreshCatalogAsync(ct);
    }

    private async Task<(int? LlmCount, int? TranscriptionCount)> RefreshCatalogAsync(
        CancellationToken ct, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? Interlocked.Read(ref _connectionRevision);
        return (
            await RefreshLlmCatalogAsync(revision, ct),
            await RefreshTranscriptionCatalogAsync(revision, ct));
    }

    private async Task<int?> RefreshLlmCatalogAsync(long revision, CancellationToken ct)
    {
        var models = await FetchLlmModelsAsync(ct);
        if (models is not { Count: > 0 })
            return null;

        return await SetFetchedLlmModelsAsync(models, revision, ct) ? models.Count : null;
    }

    private async Task<int?> RefreshTranscriptionCatalogAsync(long revision, CancellationToken ct)
    {
        var models = await FetchTranscriptionModelsAsync(ct);
        if (models is not { Count: > 0 })
            return null;

        return await SetFetchedTranscriptionModelsAsync(models, revision, ct) ? models.Count : null;
    }

    private string ResolveRequestModel(string model) =>
        string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModel ?? SupportedModels[0].Id
            : NormalizeModelId(model);

    private static OpenAiChatRequestOptions CreateRequestOptions(
        string model, string systemPrompt, string userText)
    {
        var usesReasoning = model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
        return new OpenAiChatRequestOptions
        {
            ReasoningEffort = usesReasoning ? "low" : null,
            MaxOutputTokens = usesReasoning
                ? GeminiMaxOutputTokens
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
        };
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        await _configurationGate.WaitAsync(ct);
        string? apiKey;
        long revision;
        try
        {
            apiKey = ApiKey;
            revision = Interlocked.Read(ref _connectionRevision);
        }
        finally
        {
            _configurationGate.Release();
        }
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        if (!await ValidateApiKeyAsync(apiKey, ct)
            || Interlocked.Read(ref _connectionRevision) != revision)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var (llmCount, transcriptionCount) = await RefreshCatalogAsync(ct, revision);
        if (Interlocked.Read(ref _connectionRevision) != revision)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        return new PluginSettingsValidationResult(
            true,
            llmCount is not null || transcriptionCount is not null
                ? Loc.L(
                    "Settings.ApiKeyValidFetched",
                    llmCount ?? _fetchedLlmModels.Count,
                    transcriptionCount ?? _fetchedTranscriptionModels.Count)
                : Loc.L("Settings.ApiKeyValid"));
    }
}

internal enum GeminiTranscriptionMode
{
    Smart,
    Verbatim,
}

internal sealed record GeminiFetchedModel(string Id, string? DisplayName);

internal sealed record GeminiFetchedTranscriptionModel(
    string Id,
    string? DisplayName,
    string? LiveModelId);

// ReSharper disable once ClassNeverInstantiated.Global -- deserialized from the native models endpoint
internal sealed record GeminiNativeModelsResponse(
    [property: JsonPropertyName("models")] List<GeminiNativeModel?>? Models,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

// ReSharper disable once ClassNeverInstantiated.Global -- deserialized from the native models endpoint
internal sealed record GeminiNativeModel(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("baseModelId")] string? BaseModelId,
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record GeminiCompatibleModelsResponse(
    [property: JsonPropertyName("data")] List<GeminiCompatibleModel?>? Data);

// ReSharper disable once ClassNeverInstantiated.Global -- deserialized from the models endpoint
internal sealed record GeminiCompatibleModel(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);
