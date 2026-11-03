// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed class SpeechmaticsPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    internal const string DefaultRegionId = "eu";
    private const string RegionSettingKey = "region";
    private const string LiveLanguageSettingKey = "liveLanguage";
    // Speechmatics silently drops entries over 6 words; the other caps match upstream.
    private static readonly DictionaryTermsBudget s_vocabularyBudget =
        new(MaxTerms: 100, MaxWordsPerTerm: 6, MaxTotalChars: 4_000);

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("enhanced", "Speechmatics Enhanced"),
        new("standard", "Speechmatics Standard"),
    ];

    // Every job request must go to the host that created the job, so both hosts follow one setting.
    internal static IReadOnlyList<SpeechmaticsRegion> AvailableRegions { get; } =
    [
        new(DefaultRegionId, "Settings.RegionEu", "https://eu1.asr.api.speechmatics.com/v2", "wss://eu.rt.speechmatics.com/v2"),
        new("us", "Settings.RegionUs", "https://us1.asr.api.speechmatics.com/v2", "wss://us.rt.speechmatics.com/v2"),
        new("au", "Settings.RegionAu", "https://au1.asr.api.speechmatics.com/v2", "wss://au.rt.speechmatics.com/v2"),
    ];

    // Enhanced and Standard share one list. "zh" is the app's tag for Mandarin, sent as "cmn".
    private static readonly IReadOnlyList<string> s_languages =
    [
        "ar", "ba", "eu", "be", "bn", "bg", "yue", "ca", "hr", "cs", "da", "nl", "en", "eo", "et",
        "fi", "fr", "gl", "de", "el", "he", "hi", "hu", "id", "ia", "ga", "it", "ja", "ko", "lv",
        "lt", "ms", "mt", "cmn", "zh", "mr", "mn", "no", "fa", "pl", "pt", "ro", "ru", "sk", "sl",
        "es", "sw", "sv", "tl", "ta", "th", "tr", "uk", "ur", "ug", "vi", "cy",
    ];

    // The live fallback is chosen in plugin settings, so it can also offer the bilingual packs.
    private static readonly IReadOnlyList<(string Code, string Name)> s_liveLanguages =
    [
        ("ar", "Arabic"), ("ar_en", "Arabic + English"), ("ba", "Bashkir"), ("eu", "Basque"),
        ("be", "Belarusian"), ("bn", "Bengali"), ("bg", "Bulgarian"), ("yue", "Cantonese"),
        ("ca", "Catalan"), ("hr", "Croatian"), ("cs", "Czech"), ("da", "Danish"), ("nl", "Dutch"),
        ("en", "English"), ("eo", "Esperanto"), ("et", "Estonian"), ("fi", "Finnish"),
        ("fr", "French"), ("gl", "Galician"), ("de", "German"), ("el", "Greek"), ("he", "Hebrew"),
        ("hi", "Hindi"), ("hu", "Hungarian"), ("id", "Indonesian"), ("ia", "Interlingua"),
        ("ga", "Irish"), ("it", "Italian"), ("ja", "Japanese"), ("ko", "Korean"),
        ("lv", "Latvian"), ("lt", "Lithuanian"), ("ms", "Malay"), ("en_ms", "English + Malay"),
        ("mt", "Maltese"), ("cmn", "Mandarin"), ("cmn_en", "Mandarin + English"),
        ("cmn_en_ms_ta", "Mandarin + English + Malay + Tamil"), ("mr", "Marathi"),
        ("mn", "Mongolian"), ("no", "Norwegian"), ("fa", "Persian"), ("pl", "Polish"),
        ("pt", "Portuguese"), ("ro", "Romanian"), ("ru", "Russian"), ("sk", "Slovak"),
        ("sl", "Slovenian"), ("es", "Spanish"), ("sw", "Swahili"), ("sv", "Swedish"),
        ("tl", "Tagalog + English"), ("ta", "Tamil"), ("en_ta", "English + Tamil"), ("th", "Thai"),
        ("tr", "Turkish"), ("uk", "Ukrainian"), ("ur", "Urdu"), ("ug", "Uyghur"),
        ("vi", "Vietnamese"), ("cy", "Welsh"),
    ];

    public SpeechmaticsPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
    }

    internal SpeechmaticsPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string PluginId => "com.typewhisper.speechmatics";
    public string PluginName => "Speechmatics";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        var savedModel = host.GetSetting<string>("selectedModel");
        SelectedModelId = s_models.Any(m => m.Id == savedModel) ? savedModel : s_models[0].Id;
        RegionId = ResolveRegion(host.GetSetting<string>(RegionSettingKey)).Id;
        var savedLiveLanguage = host.GetSetting<string>(LiveLanguageSettingKey);
        LiveLanguage = s_liveLanguages.Any(l => l.Code == savedLiveLanguage) ? savedLiveLanguage : null;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured}, region={RegionId})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "speechmatics";
    public string ProviderDisplayName => "Speechmatics";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    internal string RegionId { get; private set; } = DefaultRegionId;

    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    // Live transcription has no automatic detection, so Automatic streams in this language.
    internal string? LiveLanguage { get; private set; }

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => true;
    public bool SupportsStructuredDictionaryTerms => true;
    public DictionaryTermsBudget DictionaryTermsBudget => s_vocabularyBudget;
    public IReadOnlyList<string> SupportedLanguages => s_languages;
    // Batch jobs identify the language; live sessions use the live language setting instead.
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingAsync(language, [], ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct
    ) =>
        StartStreamingAsync(
            languageHints.FirstOrDefault(static hint => !string.IsNullOrWhiteSpace(hint)),
            ExtractVocabulary(prompt),
            ct
        );

    private async Task<IStreamingSession> StartStreamingAsync(
        string? language,
        IReadOnlyList<string> vocabulary,
        CancellationToken ct
    )
    {
        var apiKey = _apiKey;
        if (string.IsNullOrEmpty(apiKey))
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);

        return await SpeechmaticsStreamingSession.ConnectAsync(
            apiKey,
            new Uri(ResolveRegion(RegionId).RealtimeUrl),
            ResolveStreamingLanguage(language),
            SelectedModelId ?? s_models[0].Id,
            vocabulary,
            ct
        );
    }

    // Failing here leaves the recording to the batch job, which can identify the language.
    internal string ResolveStreamingLanguage(string? language) =>
        ToProviderLanguage(NormalizeLanguage(language) ?? LiveLanguage)
        ?? throw new PluginRequestException(Loc.L("Settings.LiveLanguageRequired"), PluginRequestFailureKind.Configuration);

    internal static SpeechmaticsRegion ResolveRegion(string? regionId) =>
        AvailableRegions.FirstOrDefault(r => r.Id == regionId) ?? AvailableRegions[0];

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) || normalized == "auto" ? null : normalized;
    }

    [return: NotNullIfNotNull(nameof(language))]
    private static string? ToProviderLanguage(string? language) => language == "zh" ? "cmn" : language;

    // Reported back in the app's tags; a bilingual pack names no single language.
    internal static string? ToReportedLanguage(string? language) =>
        NormalizeLanguage(language) switch
        {
            "cmn" => "zh",
            { } code when !code.Contains('_') => code,
            _ => null,
        };

    public void SelectModel(string modelId)
    {
        if (s_models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        // One snapshot per job: every request for a job must reach the host that created it.
        var apiKey = _apiKey;
        if (string.IsNullOrEmpty(apiKey))
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);
        var baseUrl = ResolveRegion(RegionId).BatchUrl;

        var requestedLanguage = NormalizeLanguage(language);
        var config = JsonSerializer.Serialize(
            BuildJobConfig(requestedLanguage, SelectedModelId ?? s_models[0].Id, ExtractVocabulary(prompt))
        );

        using var submitContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        submitContent.Add(fileContent, "data_file", "audio.wav");
        submitContent.Add(new StringContent(config, Encoding.UTF8, "application/json"), "config");

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/jobs");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        submitRequest.Content = submitContent;

        using var submitResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, submitRequest, HttpCompletionOption.ResponseContentRead, ct,
            // Status + reason only: the body can echo upload metadata and partial transcripts.
            (errorResponse, _) =>
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Speechmatics submit error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase})"
                );
                return $"Speechmatics API error {(int)errorResponse.StatusCode}: {errorResponse.ReasonPhrase}";
            });
        var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);

        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId =
            submitDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No job ID in Speechmatics response");

        var transcript = await PollForTranscriptAsync(
            baseUrl,
            apiKey,
            Uri.EscapeDataString(jobId),
            requestedLanguage,
            ct
        );
        return transcript;
    }

    internal static Dictionary<string, object> BuildJobConfig(
        string? language,
        string model,
        IReadOnlyList<string> vocabulary
    )
    {
        var transcriptionConfig = new Dictionary<string, object>
        {
            ["language"] = ToProviderLanguage(language) ?? "auto",
            // "model" replaces the deprecated "operating_point".
            ["model"] = model,
        };
        AddAdditionalVocab(transcriptionConfig, vocabulary);

        var jobConfig = new Dictionary<string, object>
        {
            ["type"] = "transcription",
            ["transcription_config"] = transcriptionConfig,
        };
        if (language is null)
        {
            // Dictations are far shorter than the minute identification wants; keep low-confidence guesses.
            jobConfig["language_identification_config"] = new Dictionary<string, string>
            {
                ["low_confidence_action"] = "allow",
            };
        }

        return jobConfig;
    }

    private async Task<PluginTranscriptionResult> PollForTranscriptAsync(
        string baseUrl,
        string apiKey,
        string jobId,
        string? requestedLanguage,
        CancellationToken ct
    )
    {
        const int maxAttempts = 120;

        for (var i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/jobs/{jobId}"
            );
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var statusResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, statusRequest, HttpCompletionOption.ResponseContentRead, ct,
                // Status + reason only: the body can echo upload metadata and partial transcripts.
                (errorResponse, _) =>
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Speechmatics status error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase}) for job {jobId}"
                    );
                    return $"Speechmatics status error {(int)errorResponse.StatusCode} for job {jobId}: {errorResponse.ReasonPhrase}";
                });
            var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);

            using var statusDoc = JsonDocument.Parse(statusJson);
            var job = statusDoc.RootElement.GetProperty("job");
            var status = job.GetProperty("status").GetString();

            switch (status)
            {
                case "running":
                    continue;
                case "rejected":
                    return RejectedJobResult(job, jobId);
                // Deleted and expired jobs never produce a transcript.
                case not "done":
                    throw new InvalidOperationException($"Speechmatics job {jobId} {status}");
            }

            using var transcriptRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/jobs/{jobId}/transcript?format=json-v2"
            );
            transcriptRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var transcriptResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, transcriptRequest, HttpCompletionOption.ResponseContentRead, ct,
                // Status + reason only: the body can echo upload metadata and partial transcripts.
                (errorResponse, _) =>
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Speechmatics transcript error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase}) for job {jobId}"
                    );
                    return $"Speechmatics transcript error {(int)errorResponse.StatusCode} for job {jobId}: {errorResponse.ReasonPhrase}";
                });
            var transcriptJson = await transcriptResponse.Content.ReadAsStringAsync(ct);

            return ParseTranscript(transcriptJson, job, requestedLanguage);
        }

        throw new TimeoutException(
            $"Speechmatics job {jobId} did not complete within {maxAttempts * PollInterval.TotalSeconds}s"
        );
    }

    // A no-speech rejection is an empty transcript, not a failure. Other reasons (such as an
    // unsupported identified language) carry no user content, so they are passed on.
    internal static PluginTranscriptionResult RejectedJobResult(JsonElement job, string jobId)
    {
        var reasons = job.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? errors.EnumerateArray()
                .Select(error => TryGetString(error, "message"))
                .OfType<string>()
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList()
            : [];

        if (reasons.Any(message => message.StartsWith("No speech found", StringComparison.OrdinalIgnoreCase)))
            return new PluginTranscriptionResult("", null, JobDuration(job), NoSpeechProbability: null);

        var reason = reasons.FirstOrDefault();
        throw new PluginRequestException(
            reason is null
                ? $"Speechmatics job {jobId} rejected"
                : $"Speechmatics job {jobId} rejected: {(reason.Length > 200 ? reason[..200] : reason)}",
            PluginRequestFailureKind.InvalidRequest
        );
    }

    internal static PluginTranscriptionResult ParseTranscript(string json, JsonElement job, string? requestedLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new PluginRequestException(
                "Speechmatics returned a transcript without results.",
                PluginRequestFailureKind.OutputIncomplete
            );

        root.TryGetProperty("metadata", out var metadata);
        var delimiter = TryGetString(TryGetObject(metadata, "language_pack_info"), "word_delimiter") ?? " ";

        // Results are single words and punctuation marks; attaches_to says which side of a mark
        // drops the delimiter (a missing value attaches to the previous word).
        var sb = new StringBuilder();
        var previousAttachesNext = false;
        string? detectedLanguage = null;
        foreach (var result in results.EnumerateArray())
        {
            var type = TryGetString(result, "type");
            if (type is not ("word" or "punctuation"))
                continue;

            if (
                !result.TryGetProperty("alternatives", out var alts)
                || alts.ValueKind != JsonValueKind.Array
                || alts.GetArrayLength() == 0
            )
                continue;

            var content = TryGetString(alts[0], "content");
            if (string.IsNullOrEmpty(content))
                continue;

            var attachment = TryGetString(result, "attaches_to");
            var isPunctuation = type == "punctuation";
            var attachesPrevious = isPunctuation && attachment is not ("next" or "none");
            if (sb.Length > 0 && !previousAttachesNext && !attachesPrevious)
                sb.Append(delimiter);
            sb.Append(content);
            previousAttachesNext = isPunctuation && attachment is "next" or "both";

            // Enhanced and Standard tag every word with the language the job ran in.
            if (!isPunctuation)
                detectedLanguage ??= ToReportedLanguage(TryGetString(alts[0], "language"));
        }

        detectedLanguage ??= ToReportedLanguage(
            TryGetString(TryGetObject(metadata, "transcription_config"), "language")
        );
        detectedLanguage ??= requestedLanguage;

        return new PluginTranscriptionResult(
            sb.ToString().Trim(),
            detectedLanguage,
            JobDuration(job),
            NoSpeechProbability: null
        );
    }

    private static double JobDuration(JsonElement job) =>
        job.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
            ? duration.GetDouble()
            : 0;

    private static JsonElement TryGetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? TryGetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Speechmatics has no prompt field; API prompt text (possibly the host's language-hint sentence)
    // is not vocabulary, so it stays unsent as before.
    internal static IReadOnlyList<string> ExtractVocabulary(string? prompt) =>
        PluginDictionaryTerms.Clip(PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms, s_vocabularyBudget);

    // Batch jobs and live StartRecognition both take the terms inside transcription_config.
    internal static void AddAdditionalVocab(Dictionary<string, object> transcriptionConfig, IReadOnlyList<string> vocabulary)
    {
        if (vocabulary.Count > 0)
            transcriptionConfig["additional_vocab"] = vocabulary
                .Select(static term => new Dictionary<string, string> { ["content"] = term })
                .ToArray();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);

            _host.NotifyCapabilitiesChanged();
        }
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                null,
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
            new(
                RegionSettingKey,
                Loc.L("Settings.Region"),
                Description: Loc.L("Settings.RegionDescription"),
                Options: AvailableRegions.Select(r => new PluginSettingOption(r.Id, Loc.L(r.LabelKey))).ToList()
            ),
            new(
                LiveLanguageSettingKey,
                Loc.L("Settings.LiveLanguage"),
                Description: Loc.L("Settings.LiveLanguageDescription"),
                Options:
                [
                    new PluginSettingOption("", Loc.L("Settings.LiveLanguageNone")),
                    .. s_liveLanguages.Select(l => new PluginSettingOption(l.Code, l.Name)),
                ]
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => _apiKey,
                "selectedModel" => SelectedModelId,
                RegionSettingKey => RegionId,
                LiveLanguageSettingKey => LiveLanguage ?? "",
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
            case RegionSettingKey:
                SetRegion(value);
                break;
            case LiveLanguageSettingKey:
                SetLiveLanguage(value);
                break;
        }
    }

    internal void SetRegion(string? regionId)
    {
        var region = AvailableRegions.FirstOrDefault(r => r.Id == regionId)
            ?? throw new ArgumentException($"Unknown region: {regionId}");
        RegionId = region.Id;
        _host?.SetSetting(RegionSettingKey, region.Id);
    }

    internal void SetLiveLanguage(string? language)
    {
        var code = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        if (code is not null && s_liveLanguages.All(l => l.Code != code))
            throw new ArgumentException($"Unknown live transcription language: {code}");
        LiveLanguage = code;
        _host?.SetSetting(LiveLanguageSettingKey, code ?? "");
    }
}

internal sealed record SpeechmaticsRegion(string Id, string LabelKey, string BatchUrl, string RealtimeUrl);
