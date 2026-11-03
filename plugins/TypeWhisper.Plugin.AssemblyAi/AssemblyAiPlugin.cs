// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed class AssemblyAiPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.assemblyai.com";
    // Off by default: keyterms cost extra on recorded audio and Universal-Streaming English.
    internal const string UseDictionaryTermsSettingName = "useDictionaryTerms";
    // Off by default: labels cost extra, and live text cannot carry them.
    internal const string SpeakerLabelsSettingName = "speakerLabels";

    // Streaming counts terms, not words; it ignores longer keyterms and rejects more than 100.
    private static readonly DictionaryTermsBudget s_streamingBudget =
        new(MaxTerms: 100, MaxCharsPerTerm: 50, MaxWordsPerTerm: 6);
    // Streaming keyterms go in the WebSocket URL; this keeps it inside common request-line limits.
    internal const int MaxStreamingKeytermsQueryLength = 6000;

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        .. AssemblyAiModels.All.Select(model => new PluginModelInfo(model.Id, model.DisplayName)
        {
            LanguageCount = model.Languages.Count,
            IsRecommended = model.IsPro,
        }),
    ];

    public AssemblyAiPlugin()
        : this(new HttpClient()) { }

    internal AssemblyAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.assemblyai";
    public string PluginName => "AssemblyAI";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        var savedModelId = host.GetSetting<string>("selectedModel");
        SelectedModelId = AssemblyAiModels.Resolve(savedModelId).Id;
        if (savedModelId is not null && savedModelId != SelectedModelId)
            host.SetSetting("selectedModel", SelectedModelId);
        UseDictionaryTerms = host.GetSetting<bool?>(UseDictionaryTermsSettingName) ?? false;
        SpeakerLabels = host.GetSetting<bool?>(SpeakerLabelsSettingName) ?? false;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "assemblyai";
    public string ProviderDisplayName => "AssemblyAI";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; } = AssemblyAiModels.DefaultId;

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => !SpeakerLabels;
    public bool SupportsStructuredDictionaryTerms => UseDictionaryTerms;
    public DictionaryTermsBudget DictionaryTermsBudget => BudgetFor(SelectedModelId);
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => AssemblyAiModels.Resolve(SelectedModelId).Languages;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(language is null ? [] : [language], null, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct
    ) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);
        var model = AssemblyAiModels.Resolve(SelectedModelId);
        var language = AssemblyAiModels.NormalizeLanguage(
            languageHints.FirstOrDefault(static hint => !string.IsNullOrWhiteSpace(hint)));
        // The host transcribes the recording instead when a session cannot start.
        if (!AssemblyAiModels.SupportsStreamingLanguage(model, language))
            throw new NotSupportedException("Universal-2 real-time dictation needs one of its six streaming languages selected.");
        return await AssemblyAiStreamingSession.ConnectAsync(
            ApiKey!,
            model.Id,
            language,
            StreamingKeytermsFor(prompt, UseDictionaryTerms),
            ct
        );
    }

    public void SelectModel(string modelId)
    {
        var model = AssemblyAiModels.Find(modelId) ?? throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = model.Id;
        _host?.SetSetting("selectedModel", model.Id);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        // Settings can change during the upload, so the model is read once.
        if (!IsConfigured || SelectedModelId is not { } modelId)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        var speakerLabels = SpeakerLabels;
        var keyterms = KeytermsFor(prompt, UseDictionaryTerms, BudgetFor(modelId));
        var uploadUrl = await UploadAudioAsync(wavAudio, ct);
        var transcriptId = await SubmitTranscriptionAsync(
            new SubmitOptions(uploadUrl, modelId, AssemblyAiModels.NormalizeLanguage(language), keyterms, speakerLabels),
            ct);
        return await PollForResultAsync(transcriptId, speakerLabels, ct);
    }

    private async Task<string> UploadAudioAsync(byte[] wavAudio, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/upload");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"AssemblyAI upload error {(int)errorResponse.StatusCode}: {errorBody}");
        using var doc = await ReadObjectAsync(response, ct);
        var uploadUrl = RequiredString(doc.RootElement, "upload_url");
        return Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uploadUrl
            : throw InvalidResponse();
    }

    private async Task<string> SubmitTranscriptionAsync(SubmitOptions options, CancellationToken ct)
    {
        var body = CreateSubmitBody(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/transcript");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"AssemblyAI submit error {(int)errorResponse.StatusCode}: {errorBody}");
        using var doc = await ReadObjectAsync(response, ct);
        return RequiredString(doc.RootElement, "id");
    }

    internal sealed record SubmitOptions(
        string AudioUrl,
        string ModelId,
        string? Language,
        IReadOnlyList<string> Keyterms,
        bool SpeakerLabels = false
    );

    internal static Dictionary<string, object> CreateSubmitBody(SubmitOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["audio_url"] = options.AudioUrl,
            ["speech_models"] = new[] { options.ModelId },
        };

        if (options.Language is null)
            body["language_detection"] = true;
        else
            body["language_code"] = options.Language;

        if (options.Keyterms.Count > 0)
            body["keyterms_prompt"] = options.Keyterms;

        if (options.SpeakerLabels)
            body["speaker_labels"] = true;

        return body;
    }

    internal static DictionaryTermsBudget BudgetFor(string? modelId) => AssemblyAiModels.Resolve(modelId).Budget;

    // Dictionary terms stay whole; prompt text is not sent (AssemblyAI's prompt describes the audio).
    // Terms are dropped when the setting is off, so a prompt composed before a toggle cannot add them.
    internal static IReadOnlyList<string> KeytermsFor(
        string? prompt,
        bool useDictionaryTerms,
        DictionaryTermsBudget budget
    )
    {
        if (!useDictionaryTerms)
            return [];

        var terms = PluginDictionaryTerms.Clip(PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms, budget);
        var limited = new List<string>(terms.Count);
        var totalWords = 0;
        foreach (var term in terms)
        {
            totalWords += term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (totalWords > budget.MaxTerms)
                break;
            limited.Add(term);
        }

        return limited;
    }

    internal static IReadOnlyList<string> StreamingKeytermsFor(string? prompt, bool useDictionaryTerms)
    {
        if (!useDictionaryTerms)
            return [];

        var terms = PluginDictionaryTerms.Clip(PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms, s_streamingBudget);
        var limited = new List<string>(terms.Count);
        var length = "%5B%5D".Length;
        foreach (var term in terms)
        {
            length += Uri.EscapeDataString(JsonSerializer.Serialize(term)).Length
                + (limited.Count == 0 ? 0 : "%2C".Length);
            if (length > MaxStreamingKeytermsQueryLength)
                break;
            limited.Add(term);
        }

        return limited;
    }

    private async Task<PluginTranscriptionResult> PollForResultAsync(
        string transcriptId,
        bool speakerLabels,
        CancellationToken ct
    )
    {
        for (var i = 0; i < 300; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/v2/transcript/{Uri.EscapeDataString(transcriptId)}"
            );
            request.Headers.Add("Authorization", ApiKey);

            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
                (errorResponse, errorBody) => $"AssemblyAI poll error {(int)errorResponse.StatusCode}: {errorBody}");
            using var doc = await ReadObjectAsync(response, ct);
            var root = doc.RootElement;

            switch (RequiredString(root, "status"))
            {
                case "queued" or "processing":
                    continue;
                case "completed":
                    return ParseCompleted(root, speakerLabels);
                case "error":
                    throw new PluginRequestException(
                        $"AssemblyAI transcription failed: {OptionalString(root, "error") ?? "Unknown error"}",
                        PluginRequestFailureKind.InvalidRequest);
                default:
                    throw InvalidResponse();
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out after 5 minutes");
    }

    internal static PluginTranscriptionResult ParseCompleted(JsonElement root, bool speakerLabels)
    {
        // A completed recording without speech has a null text.
        if (!root.TryGetProperty("text", out var textElement)
            || textElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw InvalidResponse();

        var text = textElement.GetString() ?? "";
        var duration = Number(root, "audio_duration") ?? 0;
        // Without usable utterances, labelled transcripts keep the plain text.
        var segments = speakerLabels ? ReadUtterances(root) : [];
        // ReSharper disable once InvertIf -- inverting would duplicate the result construction below.
        if (segments.Count > 0)
        {
            text = string.Join("\n", segments.Select(static segment => segment.Text));
            duration = Math.Max(duration, segments.Max(static segment => segment.End));
        }

        return new PluginTranscriptionResult(
            text,
            OptionalString(root, "language_code"),
            duration,
            NoSpeechProbability: null
        )
        {
            Segments = segments,
        };
    }

    private static List<PluginTranscriptionSegment> ReadUtterances(JsonElement root)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("utterances", out var utterances) || utterances.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var utterance in utterances.EnumerateArray())
        {
            if (OptionalString(utterance, "text")?.Trim() is not { Length: > 0 } text)
                continue;
            // Dropping a spoken utterance would lose text, so labelled output needs every one.
            if (Number(utterance, "start") is not { } start
                || Number(utterance, "end") is not { } end
                || end < start)
                return [];

            if (OptionalString(utterance, "speaker")?.Trim() is { Length: > 0 } speaker
                && !speaker.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                text = $"Speaker {speaker}: {text}";
            // AssemblyAI times utterances in milliseconds.
            segments.Add(new PluginTranscriptionSegment(text, start / 1000, end / 1000));
        }

        return segments;
    }

    private static async Task<JsonDocument> ReadObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return doc;
        doc.Dispose();
        throw InvalidResponse();
    }

    private static PluginRequestException InvalidResponse() =>
        new("AssemblyAI returned an invalid or incomplete response.", PluginRequestFailureKind.OutputIncomplete);

    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : throw InvalidResponse();

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
        && double.IsFinite(value)
        && value >= 0
            ? value
            : null;

    internal string? ApiKey { get; private set; }
    internal bool UseDictionaryTerms { get; private set; }
    internal bool SpeakerLabels { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v2/transcript?limit=1"
        );
        request.Headers.Add("Authorization", apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                "aa...",
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
            new(
                UseDictionaryTermsSettingName,
                Loc.L("Settings.UseDictionaryTerms"),
                Description: Loc.L("Settings.UseDictionaryTermsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                SpeakerLabelsSettingName,
                Loc.L("Settings.SpeakerLabels"),
                Description: Loc.L("Settings.SpeakerLabelsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
                UseDictionaryTermsSettingName => UseDictionaryTerms ? "true" : "false",
                SpeakerLabelsSettingName => SpeakerLabels ? "true" : "false",
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
            case "api-key":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case UseDictionaryTermsSettingName:
                UseDictionaryTerms = bool.TryParse(value, out var useDictionaryTerms) && useDictionaryTerms;
                _host?.SetSetting(UseDictionaryTermsSettingName, UseDictionaryTerms);
                break;
            case SpeakerLabelsSettingName:
                var speakerLabels = bool.TryParse(value, out var parsed) && parsed;
                if (speakerLabels == SpeakerLabels)
                    break;
                SpeakerLabels = speakerLabels;
                _host?.SetSetting(SpeakerLabelsSettingName, SpeakerLabels);
                // Live text availability follows this setting.
                _host?.NotifyCapabilitiesChanged();
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }
}
