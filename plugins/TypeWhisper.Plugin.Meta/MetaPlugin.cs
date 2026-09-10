// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta;

/// <summary>
/// Provides Meta transcription and language-model capabilities.
/// </summary>
public sealed class MetaPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware, IModelCatalogProvider,
    ITranscriptionLanguageSelectionCapabilities
{
    internal const string BaseUrl = "https://api.meta.ai";
    internal const string DefaultTranscriptionModelId = "muse-voice-transcribe-1.0";
    internal const string DefaultLlmModelId = "muse-spark-1.2";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedTranscriptionModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private const string SpeakerDiarizationSettingName = "speakerDiarizationEnabled";

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new(DefaultLlmModelId, "Muse Spark 1.2"),
        new("muse-spark-1.1", "Muse Spark 1.1"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackTranscriptionModels =
    [
        new(DefaultTranscriptionModelId, "Muse Voice Transcribe 1.0"),
    ];

    private static readonly IReadOnlyDictionary<string, string> s_languageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = "Arabic",
            ["bn"] = "Bengali",
            ["nl"] = "Dutch",
            ["en"] = "English",
            ["fr"] = "French",
            ["de"] = "German",
            ["he"] = "Hebrew",
            ["iw"] = "Hebrew",
            ["hi"] = "Hindi",
            ["id"] = "Indonesian",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["kn"] = "Kannada",
            ["ko"] = "Korean",
            ["ms"] = "Malay",
            ["zh"] = "Mandarin Chinese",
            ["cmn"] = "Mandarin Chinese",
            ["mr"] = "Marathi",
            ["pl"] = "Polish",
            ["pt"] = "Portuguese",
            ["es"] = "Spanish",
            ["tl"] = "Tagalog",
            ["fil"] = "Tagalog",
            ["ta"] = "Tamil",
            ["te"] = "Telugu",
            ["th"] = "Thai",
            ["tr"] = "Turkish",
            ["vi"] = "Vietnamese",
        };

    private static readonly Dictionary<string, string> s_canonicalLanguageCodes =
        s_languageNames
            .Where(pair => pair.Key.Length == 2 && pair.Key != "iw")
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly Func<MetaRealtimeConnectionOptions, CancellationToken, Task<IStreamingSession>>
        _streamingSessionFactory;
    private IPluginHostServices? _host;
    private List<MetaFetchedModel> _fetchedLlmModels = [];
    private List<MetaFetchedModel> _fetchedTranscriptionModels = [];

    /// <summary>
    /// Initializes a new Meta plugin instance.
    /// </summary>
    public MetaPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
    {
    }

    internal MetaPlugin(
        HttpClient httpClient,
        Func<MetaRealtimeConnectionOptions, CancellationToken, Task<IStreamingSession>>?
            streamingSessionFactory = null)
    {
        _httpClient = httpClient;
        _streamingSessionFactory = streamingSessionFactory
            ?? (async (options, ct) => await MetaRealtimeStreamingSession.ConnectAsync(
                options.ApiKey,
                options.ModelId,
                options.Mode,
                options.LanguageBias,
                options.Keywords,
                ct,
                Loc));
    }

    public string PluginId => "com.typewhisper.meta";

    public string PluginName => "Meta";

    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _fetchedLlmModels = NormalizeModels(
            host.GetSetting<List<MetaFetchedModel>>(FetchedLlmModelsSettingName) ?? [],
            IsLlmModel);
        _fetchedTranscriptionModels = NormalizeModels(
            host.GetSetting<List<MetaFetchedModel>>(FetchedTranscriptionModelsSettingName) ?? [],
            IsTranscriptionModel);
        SelectedModelId = NormalizeSelection(
            host.GetSetting<string>(SelectedTranscriptionModelSettingName),
            TranscriptionModels,
            DefaultTranscriptionModelId);
        SelectedLlmModelId = NormalizeSelection(
            host.GetSetting<string>(SelectedLlmModelSettingName),
            SupportedModels,
            DefaultLlmModelId);
        ReasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        SpeakerDiarizationEnabled = host.GetSetting<bool>(SpeakerDiarizationSettingName);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "meta";

    public string ProviderDisplayName => "Meta";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels
                .Select(model => new PluginModelInfo(model.Id, DisplayName(model.Id)))
                .ToList()
            : s_fallbackTranscriptionModels;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => IsConfigured && SelectedModelId is not null;

    public IReadOnlyList<string> SupportedLanguages => s_languageNames.Keys
        .Where(code => code.Length == 2 && code != "iw")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(code => code, StringComparer.Ordinal)
        .ToList();

    public void SelectModel(string modelId)
    {
        if (TranscriptionModels.All(model =>
                !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown transcription model: {modelId}", nameof(modelId));
        }

        SelectedModelId = modelId;
        _host?.SetSetting(SelectedTranscriptionModelSettingName, modelId);
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(
            wavAudio,
            string.IsNullOrWhiteSpace(language) ? [] : [language],
            translate,
            prompt,
            ct);

    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        EnsureTranscriptionConfigured();
        if (translate)
            throw new NotSupportedException("Muse Voice Transcribe does not support translation.");
        if (wavAudio.Length == 0)
            throw new ArgumentException("No WAV audio bytes were provided.", nameof(wavAudio));

        var normalizedLanguageHints = NormalizeLanguageHints(languageHints);
        var keywords = ParseKeywords(prompt);
        var requestJson = CreateTranscriptionRequestJson(
            SelectedModelId!,
            TranscriptionMode,
            normalizedLanguageHints,
            keywords);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request");
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "audio", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/asr/transcribe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(
            json,
            FirstLanguageCode(languageHints),
            SpeakerDiarizationEnabled,
            Loc);
    }

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(
            string.IsNullOrWhiteSpace(language) ? [] : [language],
            ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct)
    {
        EnsureTranscriptionConfigured();
        return await _streamingSessionFactory(
            new MetaRealtimeConnectionOptions(
                ApiKey!,
                SelectedModelId!,
                TranscriptionMode,
                NormalizeLanguageHints(languageHints),
                ParseKeywords(prompt)),
            ct);
    }

    public string ProviderName => "Meta";

    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels
                .Select(model => new PluginModelInfo(model.Id, DisplayName(model.Id)))
                .ToList()
            : s_fallbackLlmModels;

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new PluginRequestException(
                "API key not configured",
                PluginRequestFailureKind.Configuration);
        }

        var modelId = string.IsNullOrWhiteSpace(model)
            ? SelectedLlmModelId ?? SupportedModels[0].Id
            : model;
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            maxOutputTokenParameter: "max_completion_tokens",
            reasoningEffort: ReasoningEffort,
            temperature: null);
    }

    internal string? ApiKey { get; private set; }
    internal string? SelectedLlmModelId { get; private set; }
    internal string ReasoningEffort { get; private set; } = "medium";
    internal bool SpeakerDiarizationEnabled { get; private set; }
    internal string TranscriptionMode => SpeakerDiarizationEnabled ? "DIARIZATION" : "PUSH_TO_TALK";
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    private IPluginLocalization? _injectedLocalization;
    private readonly Lock _catalogLock = new();
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private long _connectionRevision;

    public void SetLocalization(IPluginLocalization localization) => _injectedLocalization = localization;
    public bool SupportsLanguageHints => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    internal int FetchedLlmModelCount => _fetchedLlmModels.Count;
    internal int FetchedTranscriptionModelCount => _fetchedTranscriptionModels.Count;

    internal async Task SetApiKeyAsync(string? apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        IPluginHostServices? hostToNotify = null;
        await _apiKeyWriteLock.WaitAsync();
        try
        {
            if (_host is not null)
            {
                if (normalized is null)
                    await _host.DeleteSecretAsync(ApiKeySecretName);
                else
                    await _host.StoreSecretAsync(ApiKeySecretName, normalized);
            }

            lock (_catalogLock)
            {
                if (!string.Equals(ApiKey, normalized, StringComparison.Ordinal))
                {
                    ApiKey = normalized;
                    _connectionRevision++;
                    _fetchedLlmModels = [];
                    _fetchedTranscriptionModels = [];
                    _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                    _host?.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
                    NormalizeSelections(persist: true);
                    hostToNotify = _host;
                }
            }
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        hostToNotify?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model =>
                !string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unknown language model: {modelId}", nameof(modelId));
        }

        SelectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string reasoningEffort)
    {
        ReasoningEffort = NormalizeReasoningEffort(reasoningEffort);
        _host?.SetSetting(ReasoningEffortSettingName, ReasoningEffort);
    }

    internal void SetSpeakerDiarizationEnabled(bool enabled)
    {
        if (SpeakerDiarizationEnabled == enabled)
            return;

        SpeakerDiarizationEnabled = enabled;
        _host?.SetSetting(SpeakerDiarizationSettingName, enabled);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = CreateModelsRequest(apiKey);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
    }

    internal async Task<MetaModelCatalog?> RefreshAvailableModelsAsync(CancellationToken ct = default)
    {
        string apiKey;
        long revision;
        lock (_catalogLock)
        {
            if (ApiKey is null)
                return null;
            apiKey = ApiKey;
            revision = _connectionRevision;
        }

        using var request = CreateModelsRequest(apiKey);
        try
        {
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = data.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new MetaFetchedModel(
                    element.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    element.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();
            var llmModels = NormalizeModels(models, IsLlmModel);
            var transcriptionModels = NormalizeModels(models, IsTranscriptionModel);
            MetaModelCatalog catalog;
            string? selectedTranscriptionModelId;
            string? selectedLlmModelId;
            IPluginHostServices? hostToNotify;
            lock (_catalogLock)
            {
                if (revision != _connectionRevision)
                    return null;
                _fetchedLlmModels = llmModels;
                _fetchedTranscriptionModels = transcriptionModels;
                NormalizeSelections(persist: false);
                catalog = new MetaModelCatalog(llmModels, transcriptionModels);
                selectedTranscriptionModelId = SelectedModelId;
                selectedLlmModelId = SelectedLlmModelId;
                hostToNotify = _host;
            }

            // Outside the lock: NotifyCapabilitiesChanged takes the host's lock and calls back
            // into plugins, as SetApiKeyAsync's hostToNotify already accounts for.
            // ReSharper disable once InvertIf -- keeps the persist-and-notify block together.
            if (hostToNotify is not null)
            {
                hostToNotify.SetSetting(FetchedLlmModelsSettingName, llmModels);
                hostToNotify.SetSetting(FetchedTranscriptionModelsSettingName, transcriptionModels);
                hostToNotify.SetSetting(SelectedTranscriptionModelSettingName, selectedTranscriptionModelId);
                hostToNotify.SetSetting(SelectedLlmModelSettingName, selectedLlmModelId);
                hostToNotify.NotifyCapabilitiesChanged();
            }

            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (TimeoutException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (HttpRequestException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (IOException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (JsonException ex)
        {
            return ModelRefreshFailed(ex);
        }
        catch (InvalidOperationException ex)
        {
            return ModelRefreshFailed(ex);
        }
    }

    internal static bool IsLlmModel(string modelId) =>
        modelId.StartsWith("muse-spark-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTranscriptionModel(string modelId) =>
        modelId.StartsWith("muse-voice-transcribe-", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> NormalizeLanguageHints(IEnumerable<string>? languageHints)
    {
        if (languageHints is null)
            return [];

        return languageHints
            .Select(hint => TryNormalizeLanguageHint(hint, out var languageName, out _)
                ? languageName
                : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> ParseKeywords(string? prompt) => ClipKeywords(prompt);

    private static List<string> ClipKeywords(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalChars = 0;
        foreach (var term in prompt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var keyword = string.Join(" ", term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8));
            keyword = keyword[..Math.Min(keyword.Length, 100)].TrimEnd();
            if (keyword.Length == 0 || !seen.Add(keyword))
                continue;
            var remaining = 600 - totalChars;
            if (remaining == 0 || keywords.Count == 100)
                break;
            keyword = keyword[..Math.Min(keyword.Length, remaining)].TrimEnd();
            if (keyword.Length == 0)
                break;
            keywords.Add(keyword);
            totalChars += keyword.Length;
        }
        return keywords;
    }

    internal static string CreateTranscriptionRequestJson(
        string modelId,
        string mode,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["audioEncoding"] = "WAV",
            ["mode"] = mode,
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;
        if (keywords.Count > 0)
            body["keywords"] = keywords;

        return JsonSerializer.Serialize(body);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? requestedLanguage,
        bool includeSpeakerLabels = false,
        IPluginLocalization? localization = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var transcript = root.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString()?.Trim() ?? ""
            : "";
        var durationSeconds = root.TryGetProperty("audioDurationMs", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            ? durationElement.GetDouble() / 1000d
            : 0d;
        var segments = new List<PluginTranscriptionSegment>();
        if (root.TryGetProperty("turns", out var turnsElement)
            && turnsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turnsElement.EnumerateArray())
            {
                var text = turn.TryGetProperty("transcript", out var textElement)
                    ? textElement.GetString()?.Trim() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (includeSpeakerLabels
                    && turn.TryGetProperty("speaker", out var speakerElement)
                    && NormalizeSpeakerLabel(speakerElement.GetString(), localization) is { } speaker)
                {
                    text = $"{speaker}: {text}";
                }
                var start = turn.TryGetProperty("startMs", out var startElement)
                    && startElement.ValueKind == JsonValueKind.Number
                    ? startElement.GetDouble() / 1000d
                    : 0d;
                var end = turn.TryGetProperty("endMs", out var endElement)
                    && endElement.ValueKind == JsonValueKind.Number
                    ? endElement.GetDouble() / 1000d
                    : 0d;
                segments.Add(new PluginTranscriptionSegment(text, start, end));
            }
        }

        if (includeSpeakerLabels && segments.Count > 0)
            transcript = string.Join("\n", segments.Select(segment => segment.Text));

        return new PluginTranscriptionResult(
            transcript,
            requestedLanguage,
            durationSeconds,
            NoSpeechProbability: null)
        {
            Segments = segments,
        };
    }

    internal static string? NormalizeSpeakerLabel(string? value, IPluginLocalization? localization = null)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        if (trimmed.StartsWith("Speaker ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[8..].Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : localization?.GetString("Transcript.SpeakerLabel", trimmed) ?? $"Speaker {trimmed}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }

    private static HttpRequestMessage CreateModelsRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private void EnsureTranscriptionConfigured()
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(SelectedModelId))
        {
            throw new PluginRequestException(
                "API key and transcription model are required",
                PluginRequestFailureKind.Configuration);
        }
    }

    private void NormalizeSelections(bool persist)
    {
        SelectedModelId = NormalizeSelection(
            SelectedModelId,
            TranscriptionModels,
            DefaultTranscriptionModelId);
        SelectedLlmModelId = NormalizeSelection(
            SelectedLlmModelId,
            SupportedModels,
            DefaultLlmModelId);
        if (!persist || _host is null)
            return;

        _host.SetSetting(SelectedTranscriptionModelSettingName, SelectedModelId);
        _host.SetSetting(SelectedLlmModelSettingName, SelectedLlmModelId);
    }

    private static string? NormalizeSelection(
        string? selection,
        IReadOnlyList<PluginModelInfo> models,
        string preferredDefault)
    {
        if (!string.IsNullOrWhiteSpace(selection)
            && models.Any(model => model.Id.Equals(selection, StringComparison.OrdinalIgnoreCase)))
        {
            return models.First(model =>
                model.Id.Equals(selection, StringComparison.OrdinalIgnoreCase)).Id;
        }

        return models.FirstOrDefault(model =>
                model.Id.Equals(preferredDefault, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (models.Count > 0 ? models[0].Id : null);
    }

    private static List<MetaFetchedModel> NormalizeModels(
        IEnumerable<MetaFetchedModel> models,
        Func<string, bool> predicate) =>
        models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id) && predicate(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DisplayName(string modelId) =>
        modelId switch
        {
            DefaultLlmModelId => "Muse Spark 1.2",
            "muse-spark-1.1" => "Muse Spark 1.1",
            DefaultTranscriptionModelId => "Muse Voice Transcribe 1.0",
            _ => modelId,
        };

    private static string NormalizeReasoningEffort(string? reasoningEffort) =>
        reasoningEffort is "minimal" or "low" or "medium" or "high" or "xhigh"
            ? reasoningEffort
            : "medium";

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    internal static string? FirstLanguageCode(IEnumerable<string> languageHints) =>
        languageHints
            .Select(hint => TryNormalizeLanguageHint(hint, out _, out var canonicalCode)
                ? canonicalCode
                : null)
            .FirstOrDefault(code => code is not null);

    private static bool TryNormalizeLanguageHint(
        string? rawHint,
        out string languageName,
        out string canonicalCode)
    {
        languageName = "";
        canonicalCode = "";
        var hint = rawHint?.Trim();
        if (string.IsNullOrWhiteSpace(hint)
            || hint.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseCode = hint.Split('-', '_')[0];
        if (s_languageNames.TryGetValue(baseCode, out var matchedName)
            && s_canonicalLanguageCodes.TryGetValue(matchedName, out var matchedCode))
        {
            languageName = matchedName;
            canonicalCode = matchedCode;
            return true;
        }

        if (!s_canonicalLanguageCodes.TryGetValue(hint, out var namedCode))
            return false;

        canonicalCode = namedCode;
        languageName = s_languageNames[namedCode];
        return true;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new("api-key", Loc.L("Settings.ApiKey"), IsSecret: true,
            Description: Loc.L("Settings.ApiKeyHint"), Kind: PluginSettingKind.Secret),
        new("selectedModel", Loc.L("Settings.TranscriptionModel"),
            Description: _fetchedTranscriptionModels.Count > 0
                ? Loc.L("Settings.TranscriptionModelsFetched", _fetchedTranscriptionModels.Count)
                : Loc.L("Settings.TranscriptionModelFallback"),
            Options: TranscriptionModels.Select(model => new PluginSettingOption(model.Id, model.DisplayName)).ToList(),
            Kind: PluginSettingKind.Dropdown),
        new("speakerDiarizationEnabled", Loc.L("Settings.SpeakerDiarization"),
            Description: Loc.L("Settings.SpeakerDiarizationHint"), Kind: PluginSettingKind.Boolean),
        new("selectedLlmModel", Loc.L("Settings.LlmModel"),
            Description: _fetchedLlmModels.Count > 0
                ? Loc.L("Settings.LlmModelsFetched", _fetchedLlmModels.Count)
                : Loc.L("Settings.LlmModelFallback"),
            Options: SupportedModels.Select(model => new PluginSettingOption(model.Id, model.DisplayName)).ToList(),
            Kind: PluginSettingKind.Dropdown),
        new("reasoningEffort", Loc.L("Settings.ReasoningEffort"),
            Description: Loc.L("Settings.ReasoningEffortHint"),
            Options:
            [
                new PluginSettingOption("minimal", Loc.L("Settings.ReasoningMinimal")),
                new PluginSettingOption("low", Loc.L("Settings.ReasoningLow")),
                new PluginSettingOption("medium", Loc.L("Settings.ReasoningMedium")),
                new PluginSettingOption("high", Loc.L("Settings.ReasoningHigh")),
                new PluginSettingOption("xhigh", Loc.L("Settings.ReasoningXHigh")),
            ], Kind: PluginSettingKind.Dropdown),
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            ApiKeySecretName => ApiKey,
            SelectedTranscriptionModelSettingName => SelectedModelId,
            SpeakerDiarizationSettingName => SpeakerDiarizationEnabled ? "true" : "false",
            SelectedLlmModelSettingName => SelectedLlmModelId,
            ReasoningEffortSettingName => ReasoningEffort,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value);
                break;
            case SelectedTranscriptionModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case SpeakerDiarizationSettingName:
                SetSpeakerDiarizationEnabled(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
                break;
            case SelectedLlmModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectLlmModel(value);
                break;
            case ReasoningEffortSettingName:
                SetReasoningEffort(value ?? "medium");
                break;
        }
    }

    public async Task RefreshModelCatalogAsync(CancellationToken ct = default) =>
        await RefreshAvailableModelsAsync(ct);

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ApiKey is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        try
        {
            await ValidateApiKeyAsync(ApiKey, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Invalid API key")
        {
            // This branch's shared HTTP helper maps HTTP 401 to this exception.
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException or IOException)
        {
            _host?.Log(PluginLogLevel.Warning, $"Could not verify the Meta API key: {ex.Message}");
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ValidationFailed"));
        }

        var catalog = await RefreshAvailableModelsAsync(ct);
        return new PluginSettingsValidationResult(true, catalog is null
            ? Loc.L("Settings.ApiKeyValid")
            : Loc.L("Settings.ModelsFetched", catalog.TranscriptionModels.Count, catalog.LlmModels.Count));
    }

    private MetaModelCatalog? ModelRefreshFailed(Exception ex)
    {
        _host?.Log(PluginLogLevel.Warning, $"Could not refresh Meta model catalog: {ex.Message}");
        return null;
    }
}

internal sealed record MetaRealtimeConnectionOptions(
    string ApiKey,
    string ModelId,
    string Mode,
    IReadOnlyList<string> LanguageBias,
    IReadOnlyList<string> Keywords);
