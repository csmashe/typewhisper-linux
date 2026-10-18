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
    // Off by default: keyterms cost extra on the Pro and Universal-Streaming models.
    internal const string UseDictionaryTermsSettingName = "useDictionaryTerms";

    // Recorded audio counts each word of a phrase toward the limit, so KeytermsFor caps total words.
    private static readonly DictionaryTermsBudget s_proBudget = new(MaxTerms: 1000, MaxWordsPerTerm: 6);
    private static readonly DictionaryTermsBudget s_universal2Budget = new(MaxTerms: 200, MaxWordsPerTerm: 6);
    // Streaming counts terms, not words; it ignores longer keyterms and rejects more than 100.
    private static readonly DictionaryTermsBudget s_streamingBudget =
        new(MaxTerms: 100, MaxCharsPerTerm: 50, MaxWordsPerTerm: 6);
    // Streaming keyterms go in the WebSocket URL; this keeps it inside common request-line limits.
    internal const int MaxStreamingKeytermsQueryLength = 6000;

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("universal-3-pro", "Universal-3 Pro"),
        new("universal-2", "Universal-2"),
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
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
        UseDictionaryTerms = host.GetSetting<bool?>(UseDictionaryTermsSettingName) ?? false;
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

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public bool SupportsStructuredDictionaryTerms => UseDictionaryTerms;
    public DictionaryTermsBudget DictionaryTermsBudget => BudgetFor(SelectedModelId);
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

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
        var language = languageHints.FirstOrDefault(static hint => !string.IsNullOrWhiteSpace(hint))?.Trim();
        return await AssemblyAiStreamingSession.ConnectAsync(
            ApiKey!,
            language,
            StreamingKeytermsFor(prompt, UseDictionaryTerms),
            ct
        );
    }

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
        // Settings can change during the upload, so the model is read once.
        if (!IsConfigured || SelectedModelId is not { } modelId)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        var keyterms = KeytermsFor(prompt, UseDictionaryTerms, BudgetFor(modelId));
        var uploadUrl = await UploadAudioAsync(wavAudio, ct);
        var transcriptId = await SubmitTranscriptionAsync(uploadUrl, modelId, language, keyterms, ct);
        return await PollForResultAsync(transcriptId, ct);
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
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("upload_url").GetString()
            ?? throw new InvalidOperationException("Missing upload_url in response");
    }

    private async Task<string> SubmitTranscriptionAsync(
        string audioUrl,
        string modelId,
        string? language,
        IReadOnlyList<string> keyterms,
        CancellationToken ct
    )
    {
        var body = CreateSubmitBody(audioUrl, modelId, language, keyterms);

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
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Missing id in response");
    }

    internal static Dictionary<string, object> CreateSubmitBody(
        string audioUrl,
        string modelId,
        string? language,
        IReadOnlyList<string> keyterms
    )
    {
        var body = new Dictionary<string, object>
        {
            ["audio_url"] = audioUrl,
            ["speech_models"] = new[] { modelId },
        };

        if (string.IsNullOrEmpty(language))
            body["language_detection"] = true;
        else
            body["language_code"] = language;

        if (keyterms.Count > 0)
            body["keyterms_prompt"] = keyterms;

        return body;
    }

    internal static DictionaryTermsBudget BudgetFor(string? modelId) =>
        modelId == "universal-2" ? s_universal2Budget : s_proBudget;

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
        CancellationToken ct
    )
    {
        for (var i = 0; i < 300; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/v2/transcript/{transcriptId}"
            );
            request.Headers.Add("Authorization", ApiKey);

            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
                (errorResponse, errorBody) => $"AssemblyAI poll error {(int)errorResponse.StatusCode}: {errorBody}");
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- subjective control-flow style; the if-chain reads fine here.
            if (status == "error")
            {
                var error = root.TryGetProperty("error", out var errEl)
                    ? errEl.GetString()
                    : "Unknown error";
                throw new InvalidOperationException($"AssemblyAI transcription failed: {error}");
            }

            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (status == "completed")
            {
                var text = root.GetProperty("text").GetString() ?? "";
                var duration = root.TryGetProperty("audio_duration", out var durEl)
                    ? durEl.GetDouble()
                    : 0.0;
                var detectedLanguage = root.TryGetProperty("language_code", out var langEl)
                    ? langEl.GetString()
                    : null;
                return new PluginTranscriptionResult(
                    text,
                    detectedLanguage,
                    duration,
                    NoSpeechProbability: null
                );
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out after 5 minutes");
    }

    internal string? ApiKey { get; private set; }
    internal bool UseDictionaryTerms { get; private set; }

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
            var response = await _httpClient.SendAsync(request, ct);
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
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
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
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case UseDictionaryTermsSettingName:
                UseDictionaryTerms = bool.TryParse(value, out var useDictionaryTerms) && useDictionaryTerms;
                _host?.SetSetting(UseDictionaryTermsSettingName, UseDictionaryTerms);
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
