// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

internal enum ElevenLabsTranscriptionMode { Automatic, RestOnly }
internal enum ApiKeyCheck { Valid, Invalid, Unverified, Unavailable }

public sealed class ElevenLabsPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    internal const string DefaultModelId = "scribe_v2";
    internal const string MedicalModelId = "scribe_v2_medical";
    private const string BaseUrl = "https://api.elevenlabs.io";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    internal const string TranscriptionModeSettingName = "transcriptionMode";
    internal const string TagAudioEventsSettingName = "tagAudioEvents";
    internal const string NoVerbatimSettingName = "noVerbatim";
    internal const string SpeakerCountSettingName = "numSpeakers";
    internal const int AutomaticSpeakerCount = 0;
    internal const int MaxSpeakerCount = 32;
    internal const int DefaultSpeakerCount = 1;
    // Off by default (keyterms cost 20% more): dictionary terms then reach ElevenLabs only as HTTP API comma prompts.
    internal const string UseDictionaryTermsSettingName = "useDictionaryTerms";

    private static readonly SearchValues<char> s_invalidKeytermCharacters = SearchValues.Create("<>{}[]\\");

    // Keyterm limits: https://elevenlabs.io/docs/api-reference/speech-to-text/convert
    private static readonly DictionaryTermsBudget s_dictionaryBudget = new(MaxTerms: 1000, MaxCharsPerTerm: 49, MaxWordsPerTerm: 5);

    private static readonly IReadOnlyList<ElevenLabsModelEntry> s_modelEntries =
    [
        new(DefaultModelId, "Scribe v2", "scribe_v2", "scribe_v2_realtime"),
        // Same request shape and price as Scribe v2, but batch only.
        new(MedicalModelId, "Scribe v2 Medical", MedicalModelId, RealtimeModelId: null),
    ];

    // Codes both Scribe endpoints honour. They reject "jw", "haw", "vo" and "ln" (Javanese is "jv"), and
    // accept but ignore documented ones such as "ceb", "kea", "luo" and "umb", which then auto-detect.
    private static readonly IReadOnlyList<string> s_languages =
    [
        "af",
        "am",
        "ar",
        "as",
        "ast",
        "az",
        "ba",
        "be",
        "bg",
        "bn",
        "bo",
        "br",
        "bs",
        "ca",
        "cs",
        "cy",
        "da",
        "de",
        "el",
        "en",
        "es",
        "et",
        "eu",
        "fa",
        "fi",
        "fo",
        "fr",
        "ga",
        "gl",
        "gu",
        "ha",
        "he",
        "hi",
        "hr",
        "ht",
        "hu",
        "hy",
        "id",
        "ig",
        "is",
        "it",
        "ja",
        "jv",
        "ka",
        "kk",
        "km",
        "kn",
        "ko",
        "ku",
        "ky",
        "la",
        "lb",
        "lg",
        "lo",
        "lt",
        "lv",
        "mg",
        "mi",
        "mk",
        "ml",
        "mn",
        "mr",
        "ms",
        "mt",
        "my",
        "ne",
        "nl",
        "nn",
        "no",
        "nso",
        "oc",
        "or",
        "pa",
        "pl",
        "ps",
        "pt",
        "ro",
        "ru",
        "sa",
        "sd",
        "si",
        "sk",
        "sl",
        "sn",
        "so",
        "sq",
        "sr",
        "su",
        "sv",
        "sw",
        "ta",
        "te",
        "tg",
        "th",
        "tk",
        "tl",
        "tr",
        "tt",
        "uk",
        "ur",
        "uz",
        "vi",
        "wo",
        "xh",
        "yi",
        "yo",
        "yue",
        "zh",
        "zu",
    ];

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    public ElevenLabsPlugin()
        : this(CreateHttpClient()) { }

    internal ElevenLabsPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.elevenlabs";
    public string PluginName => "ElevenLabs";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync(ApiKeySecretName);
        var savedModelId = host.GetSetting<string>(SelectedModelSettingName);
        SelectedModelId = NormalizeModelId(savedModelId);
        // A retired id such as scribe_v1 is saved as the model that actually runs.
        if (savedModelId is not null && savedModelId != SelectedModelId)
            host.SetSetting(SelectedModelSettingName, SelectedModelId);
        TranscriptionMode = (host.GetSetting<string>(TranscriptionModeSettingName) ?? "automatic") == "restOnly"
            ? ElevenLabsTranscriptionMode.RestOnly
            : ElevenLabsTranscriptionMode.Automatic;
        TagAudioEvents = host.GetSetting<bool?>(TagAudioEventsSettingName) ?? false;
        NoVerbatim = host.GetSetting<bool?>(NoVerbatimSettingName) ?? true;
        SpeakerCount = NormalizeSpeakerCount(host.GetSetting<int?>(SpeakerCountSettingName) ?? DefaultSpeakerCount);
        UseDictionaryTerms = host.GetSetting<bool?>(UseDictionaryTermsSettingName) ?? false;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "elevenlabs";
    public string ProviderDisplayName => "ElevenLabs";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_modelEntries
            .Select(m => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = m.Id == DefaultModelId })
            .ToList();

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public DictionaryTermsBudget DictionaryTermsBudget => s_dictionaryBudget;
    // The host reads this per request; realtime sessions ignore the prompt, so terms reach REST requests only.
    public bool SupportsStructuredDictionaryTerms => UseDictionaryTerms;
    // Audio events, speaker hints and Scribe v2 Medical are REST-only, so automatic mode uses the complete recording for them;
    // the host reads this live at recording start.
    public bool SupportsStreaming =>
        TranscriptionMode == ElevenLabsTranscriptionMode.Automatic
        && !TagAudioEvents
        && SpeakerCount == DefaultSpeakerCount
        && ModelStreams(SelectedModelId);
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => s_languages;

    public void SelectModel(string modelId)
    {
        var entry = ResolveModelEntry(modelId);
        var streamedBefore = SupportsStreaming;
        SelectedModelId = entry.Id;
        _host?.SetSetting(SelectedModelSettingName, entry.Id);
        if (SupportsStreaming != streamedBefore)
            _host?.NotifyCapabilitiesChanged();
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (!IsConfigured || SelectedModelId is null)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        var entry = ResolveModelEntry(SelectedModelId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/speech-to-text");
        request.Headers.TryAddWithoutValidation("xi-api-key", ApiKey);

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");
        form.Add(new StringContent(entry.RestModelId), "model_id");

        if (NormalizeLanguage(language) is { } normalizedLanguage)
            form.Add(new StringContent(normalizedLanguage), "language_code");

        form.Add(new StringContent("word"), "timestamps_granularity");
        form.Add(new StringContent(TagAudioEvents ? "true" : "false"), "tag_audio_events");
        form.Add(new StringContent(NoVerbatim ? "true" : "false"), "no_verbatim");
        if (SpeakerCount != AutomaticSpeakerCount)
            form.Add(new StringContent(SpeakerCount.ToString(CultureInfo.InvariantCulture)), "num_speakers");

        foreach (var term in ExtractKeyterms(prompt, UseDictionaryTerms))
            form.Add(new StringContent(term), "keyterms");

        request.Content = form;

        using var response = await SendTranscriptionRequestAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        return ParseRestResponse(json, NormalizeLanguage(language));
    }

    private async Task<HttpResponseMessage> SendTranscriptionRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? unauthorizedStatus = null;
        try
        {
            return await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
                (errorResponse, errorBody) =>
                {
                    if (errorResponse.StatusCode != HttpStatusCode.Unauthorized)
                        return $"ElevenLabs API error {(int)errorResponse.StatusCode}: {errorBody}";

                    unauthorizedStatus = ReadErrorDetail(errorBody).Status;
                    return UnauthorizedMessage(unauthorizedStatus);
                });
        }
        // ElevenLabs also answers 401 when the key's credit quota is used up or it lacks a permission.
        catch (PluginRequestException ex) when (unauthorizedStatus is "quota_exceeded" or "missing_permissions")
        {
            throw new PluginRequestException(
                ex.Message, PluginRequestFailureKind.Permission, ex.HttpStatusCode, ex.RetryAfter, innerException: ex);
        }
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || SelectedModelId is null)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        var entry = ResolveModelEntry(SelectedModelId);
        if (entry.RealtimeModelId is not { } realtimeModelId)
            throw new PluginRequestException(
                $"{entry.DisplayName} has no live transcription.", PluginRequestFailureKind.Configuration
            );

        return await ElevenLabsStreamingSession.ConnectAsync(
            ApiKey!,
            realtimeModelId,
            NormalizeLanguage(language),
            NoVerbatim,
            ct
        );
    }

    internal ElevenLabsTranscriptionMode TranscriptionMode { get; private set; } = ElevenLabsTranscriptionMode.Automatic;
    internal bool TagAudioEvents { get; private set; }
    internal bool NoVerbatim { get; private set; } = true;
    internal int SpeakerCount { get; private set; } = DefaultSpeakerCount;
    internal bool UseDictionaryTerms { get; private set; }

    internal static int NormalizeSpeakerCount(int count) => Math.Clamp(count, AutomaticSpeakerCount, MaxSpeakerCount);

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(ApiKey, normalized, StringComparison.Ordinal);

        ApiKey = normalized;
        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            if (changed && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<ApiKeyCheck> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/user");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return ApiKeyCheck.Valid;

            // Rate limits and server errors say nothing about the key itself.
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when IsUserReadPermissionOnly(await response.Content.ReadAsStringAsync(ct))
                    => ApiKeyCheck.Unverified,
                HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => ApiKeyCheck.Unavailable,
                _ => ApiKeyCheck.Invalid,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // Transport failure or client timeout: the key was never checked.
            return ApiKeyCheck.Unavailable;
        }
    }

    internal static bool IsUserReadPermissionOnly(string json)
    {
        var (status, message) = ReadErrorDetail(json);
        return status == "missing_permissions"
            && string.Equals(
                message?.Trim(),
                "The API key you used is missing the permission user_read to execute this operation.",
                StringComparison.OrdinalIgnoreCase);
    }

    internal static (string? Status, string? Message) ReadErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("detail", out var detail)
                || detail.ValueKind != JsonValueKind.Object
            )
            {
                return (null, null);
            }

            return (
                detail.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null,
                detail.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // Fixed texts: ElevenLabs' own 401 wording is never passed on.
    private string UnauthorizedMessage(string? status) =>
        status switch
        {
            "quota_exceeded" => Loc.L("Errors.QuotaExceeded"),
            "missing_permissions" => Loc.L("Errors.MissingSpeechToTextPermission"),
            _ => Loc.L("Errors.ApiKeyRejected"),
        };

    internal static PluginTranscriptionResult ParseRestResponse(
        string json,
        string? fallbackLanguage
    )
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl)
            ? textEl.GetString()?.Trim() ?? ""
            : "";
        var detectedLanguage = root.TryGetProperty("language_code", out var langEl)
            ? ElevenLabsLanguageCodes.Canonicalize(langEl.GetString()) ?? fallbackLanguage
            : fallbackLanguage;

        var entries = new List<ElevenLabsTranscriptEntry>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                if (ReadEntry(wordEl) is { } entry)
                    entries.Add(entry);
            }
        }

        var segments = ElevenLabsSegmentBuilder.Build(entries);
        var duration = segments.Count == 0 ? 0 : segments.Max(segment => segment.End);

        return new PluginTranscriptionResult(
            text,
            detectedLanguage,
            duration,
            NoSpeechProbability: null
        )
        {
            Segments = segments,
        };
    }

    // Scribe returns one entry per word, per space between words and per tagged audio event. Entries with
    // missing or inconsistent times are dropped; spacing keeps its text so segment texts join to the transcript.
    private static ElevenLabsTranscriptEntry? ReadEntry(JsonElement element)
    {
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        ElevenLabsEntryKind? kind = element.TryGetProperty("type", out var typeEl)
            ? typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString() switch
                {
                    "word" => ElevenLabsEntryKind.Word,
                    "spacing" => ElevenLabsEntryKind.Spacing,
                    "audio_event" => ElevenLabsEntryKind.AudioEvent,
                    _ => null,
                }
                : null
            : ElevenLabsEntryKind.Word;
        if (kind is not { } entryKind)
            return null;

        var text = textEl.GetString() ?? "";
        if (entryKind == ElevenLabsEntryKind.Spacing)
            return new ElevenLabsTranscriptEntry(entryKind, text, 0, 0);

        if (
            string.IsNullOrWhiteSpace(text)
            || !TryGetDouble(element, "start", out var start)
            || !TryGetDouble(element, "end", out var end)
            || !double.IsFinite(start)
            || !double.IsFinite(end)
            || start < 0
            || end < start
        )
        {
            return null;
        }

        return new ElevenLabsTranscriptEntry(entryKind, text, start, end);
    }

    // Prompt text (an API caller's prompt or the legacy comma list) is split; dictionary terms stay whole.
    internal static IReadOnlyList<string> ExtractKeyterms(string? prompt, bool includeDictionaryTerms = true)
    {
        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        // The HTTP API merges prompt parts with newlines, so a newline must separate terms.
        var textTerms = parsed.Text?.Split(
            [',', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        ) ?? [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var term in textTerms.Concat(includeDictionaryTerms ? parsed.DictionaryTerms : []))
        {
            if (
                term.Length == 0
                || term.Length >= 50
                || term.AsSpan().IndexOfAny(s_invalidKeytermCharacters) >= 0
                || term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 5
                || !seen.Add(term)
            )
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == 1000)
                break;
        }

        return terms;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                "xi-...",
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_modelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
            new(
                TranscriptionModeSettingName,
                Loc.L("Settings.TranscriptionMode"),
                Description: Loc.L("Settings.TranscriptionModeDescription"),
                Options:
                [
                    new PluginSettingOption("automatic", Loc.L("Settings.ModeAutomatic")),
                    new PluginSettingOption("restOnly", Loc.L("Settings.ModeRestOnly")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                NoVerbatimSettingName,
                Loc.L("Settings.NoVerbatim"),
                Description: Loc.L("Settings.NoVerbatimDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                TagAudioEventsSettingName,
                Loc.L("Settings.TagAudioEvents"),
                Description: Loc.L("Settings.TagAudioEventsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                SpeakerCountSettingName,
                Loc.L("Settings.SpeakerCount"),
                Placeholder: "0 – 32",
                Description: Loc.L("Settings.SpeakerCountDescription"),
                Kind: PluginSettingKind.Text
            ),
            new(
                UseDictionaryTermsSettingName,
                Loc.L("Settings.UseDictionaryTerms"),
                Description: Loc.L("Settings.UseDictionaryTermsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
                TranscriptionModeSettingName => TranscriptionMode == ElevenLabsTranscriptionMode.RestOnly ? "restOnly" : "automatic",
                NoVerbatimSettingName => NoVerbatim ? "true" : "false",
                TagAudioEventsSettingName => TagAudioEvents ? "true" : "false",
                SpeakerCountSettingName => SpeakerCount.ToString(CultureInfo.InvariantCulture),
                UseDictionaryTermsSettingName => UseDictionaryTerms ? "true" : "false",
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
            case TranscriptionModeSettingName:
                if (value is not ("automatic" or "restOnly"))
                    break;
                var mode = value == "restOnly" ? ElevenLabsTranscriptionMode.RestOnly : ElevenLabsTranscriptionMode.Automatic;
                var changed = TranscriptionMode != mode;
                TranscriptionMode = mode;
                _host?.SetSetting(TranscriptionModeSettingName, value);
                if (changed)
                    _host?.NotifyCapabilitiesChanged();
                break;
            case TagAudioEventsSettingName:
                TagAudioEvents = ParseBool(value);
                _host?.SetSetting(TagAudioEventsSettingName, TagAudioEvents);
                break;
            case NoVerbatimSettingName:
                NoVerbatim = ParseBool(value);
                _host?.SetSetting(NoVerbatimSettingName, NoVerbatim);
                break;
            case SpeakerCountSettingName:
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    SpeakerCount = NormalizeSpeakerCount(count);
                    _host?.SetSetting(SpeakerCountSettingName, SpeakerCount);
                }
                break;
            case UseDictionaryTermsSettingName:
                UseDictionaryTerms = ParseBool(value);
                _host?.SetSetting(UseDictionaryTermsSettingName, UseDictionaryTerms);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var check = await ValidateApiKeyAsync(ApiKey, ct);
        return check switch
        {
            ApiKeyCheck.Valid => new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid")),
            ApiKeyCheck.Unverified => new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyUnverified")),
            ApiKeyCheck.Unavailable => new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyUnavailable")),
            _ => new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid")),
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language)
            ? null
            : language;

    internal static bool ModelStreams(string? modelId) =>
        s_modelEntries.FirstOrDefault(m => m.Id == modelId)?.RealtimeModelId is not null;

    private static string NormalizeModelId(string? modelId) =>
        s_modelEntries.Any(m => m.Id == modelId) ? modelId! : DefaultModelId;

    private static ElevenLabsModelEntry ResolveModelEntry(string modelId) =>
        s_modelEntries.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (
            element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
        )
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    private sealed record ElevenLabsModelEntry(
        string Id,
        string DisplayName,
        string RestModelId,
        string? RealtimeModelId
    );
}
