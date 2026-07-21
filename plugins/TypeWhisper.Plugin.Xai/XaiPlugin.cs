// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

public sealed class XaiPlugin
    : ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        ITtsProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.x.ai";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels";
    private const string SelectedVoiceSettingName = "selectedVoice";
    private const string FetchedVoicesSettingName = "fetchedVoices";
    private const string CustomVoiceIdSettingName = "customVoiceId";
    private const string TtsLowLatencySettingName = "ttsLowLatency";
    private const string TtsTextNormalizationSettingName = "ttsTextNormalization";

    internal const string DefaultLlmModelId = "grok-4.3";
    internal const string DefaultSttModelId = "grok-stt";

    private static readonly IReadOnlyList<PluginModelInfo> s_sttModels =
    [
        new(DefaultSttModelId, "Grok Speech to Text"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new(DefaultLlmModelId, "Grok 4.3"),
    ];

    private static readonly IReadOnlyList<string> s_languages =
    [
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi",
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<byte[], ITtsPlaybackSession> _ttsPlaybackFactory;
    private readonly Func<bool> _ttsPlaybackAvailableProbe;
    private IPluginHostServices? _host;
    private List<XaiFetchedModel> _fetchedLlmModels = [];
    private string? _selectedVoiceId;
    private List<XaiFetchedVoice> _fetchedVoices = [];
    private bool _streamResponses = true;

    public XaiPlugin()
        : this(CreateHttpClient())
    {
    }

    internal XaiPlugin(
        HttpClient httpClient,
        Func<byte[], ITtsPlaybackSession>? ttsPlaybackFactory = null,
        Func<bool>? ttsPlaybackAvailableProbe = null)
    {
        _httpClient = httpClient;
        _ttsPlaybackFactory = ttsPlaybackFactory
            ?? (pcm => XaiPcmTtsPlaybackSession.Create(pcm, XaiTtsConfiguration.SampleRate));
        _ttsPlaybackAvailableProbe = ttsPlaybackAvailableProbe
            ?? XaiPcmTtsPlaybackSession.IsPlaybackAvailable;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.xai";
    public string PluginName => "xAI / Grok";
    public string PluginVersion => "1.1.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        SelectedModelId = NormalizeSttModelId(host.GetSetting<string>(SelectedModelSettingName));
        SelectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName) ?? DefaultLlmModelId;
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<XaiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        _fetchedVoices = NormalizeFetchedVoices(
            host.GetSetting<List<XaiFetchedVoice>>(FetchedVoicesSettingName) ?? []);
        CustomVoiceId = host.GetSetting<string>(CustomVoiceIdSettingName)?.Trim() ?? "";
        TtsLowLatency = host.GetSetting<bool?>(TtsLowLatencySettingName) ?? false;
        TtsTextNormalization = host.GetSetting<bool?>(TtsTextNormalizationSettingName) ?? false;
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;

        NormalizeSelectedLlmModel(persist: false);
        NormalizeSelectedVoice(persist: false);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "xai";
    public string ProviderDisplayName => "xAI / Grok";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_sttModels;
    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public IReadOnlyList<string> SupportedLanguages => s_languages;

    public void SelectModel(string modelId)
    {
        SelectedModelId = NormalizeSttModelId(modelId);
        _host?.SetSetting(SelectedModelSettingName, SelectedModelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("xAI STT does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        using var form = new MultipartFormDataContent();
        var normalizedLanguage = NormalizeLanguage(language);
        if (normalizedLanguage is not null)
        {
            form.Add(new StringContent("true"), "format");
            form.Add(new StringContent(normalizedLanguage), "language");
        }

        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/stt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = form;

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseSttResponse(json, normalizedLanguage);
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        // Run through the same normalization the batch TranscribeAsync uses
        // so a setting value like " de " or "auto" doesn't propagate into the
        // streaming URI as %20de%20 or language=auto.
        return await XaiStreamingSession.ConnectAsync(ApiKey!, NormalizeLanguage(language), ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "xAI / Grok";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : s_fallbackLlmModels;

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var modelId = string.IsNullOrWhiteSpace(model)
            ? SelectedLlmModelId ?? SupportedModels[0].Id
            : model;
        var client = new XaiResponsesClient(_httpClient, BaseUrl, ApiKey!);
        return await client.ProcessAsync(systemPrompt, userText, modelId, ct);
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!_streamResponses)
        {
            yield return await ProcessAsync(systemPrompt, userText, model, ct);
            yield break;
        }

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var modelId = string.IsNullOrWhiteSpace(model)
            ? SelectedLlmModelId ?? SupportedModels[0].Id
            : model;
        var client = new XaiResponsesClient(_httpClient, BaseUrl, ApiKey!);
        var source = client.ProcessStreamingAsync(systemPrompt, userText, modelId, ct);
        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
    }

    // ITtsProviderPlugin

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices =>
        _fetchedVoices.Count > 0
            ? _fetchedVoices.Select(v => new PluginVoiceInfo(v.VoiceId, v.DisplayName, v.Language)).ToList()
            : XaiTtsConfiguration.FallbackVoices;

    public string? SelectedVoiceId =>
        !string.IsNullOrWhiteSpace(CustomVoiceId)
            ? CustomVoiceId
            : _selectedVoiceId ?? XaiTtsConfiguration.DefaultVoiceId;

    public string? SettingsSummary
    {
        get
        {
            var voice = AvailableVoices.FirstOrDefault(v => v.Id == SelectedVoiceId)?.DisplayName
                ?? SelectedVoiceId
                ?? XaiTtsConfiguration.DefaultVoiceId;
            var latency = TtsLowLatency ? "low latency" : "quality";
            return $"Voice: {voice}; {latency}";
        }
    }

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        _host?.NotifyCapabilitiesChanged();
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return XaiInactiveTtsPlaybackSession.Instance;

        // The xAI TTS endpoint is a paid request. With no audio player on PATH
        // the synthesized PCM could only be discarded (XaiPcmTtsPlaybackSession
        // would return the inactive sentinel), so skip the request entirely.
        if (!_ttsPlaybackAvailableProbe())
        {
            _host?.Log(
                PluginLogLevel.Warning,
                "Skipping xAI TTS request: no audio player (paplay/aplay) found on PATH.");
            return XaiInactiveTtsPlaybackSession.Instance;
        }

        var body = XaiTtsConfiguration.CreateRequestBody(
            text,
            SelectedVoiceId,
            NormalizeTtsLanguage(request.Language),
            TtsLowLatency,
            TtsTextNormalization);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/tts");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        httpRequest.Content = XaiJson.CreateJsonContent(body);

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, httpRequest, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        return _ttsPlaybackFactory(pcm);
    }

    // Settings support

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    internal string? SelectedLlmModelId { get; private set; }

    internal IReadOnlyList<XaiFetchedModel> FetchedLlmModels => _fetchedLlmModels;
    internal IReadOnlyList<XaiFetchedVoice> FetchedVoices => _fetchedVoices;
    internal string CustomVoiceId { get; private set; } = "";

    internal bool TtsLowLatency { get; private set; }

    internal bool TtsTextNormalization { get; private set; }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
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

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = (SupportedModels.Count > 0 ? SupportedModels[0] : null)?.Id ?? modelId;

        SelectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetFetchedLlmModels(List<XaiFetchedModel> models)
    {
        _fetchedLlmModels = NormalizeFetchedLlmModels(models);
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<XaiFetchedModel>> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(e => new XaiFetchedModel(
                    GetString(e, "id") ?? "",
                    GetString(e, "owned_by")))
                .Where(model => IsLlmModel(model.Id))
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal void SetFetchedVoices(List<XaiFetchedVoice> voices)
    {
        _fetchedVoices = NormalizeFetchedVoices(voices);
        _host?.SetSetting(FetchedVoicesSettingName, _fetchedVoices);
        NormalizeSelectedVoice(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<XaiFetchedVoice>> FetchVoicesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/tts/voices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("voices", out var voicesEl)
                || voicesEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return voicesEl.EnumerateArray()
                .Select(e => new XaiFetchedVoice(
                    GetString(e, "voice_id") ?? "",
                    GetString(e, "name"),
                    GetString(e, "language")))
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    internal void SetCustomVoiceId(string voiceId)
    {
        CustomVoiceId = voiceId.Trim();
        _host?.SetSetting(CustomVoiceIdSettingName, CustomVoiceId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTtsLowLatency(bool enabled)
    {
        TtsLowLatency = enabled;
        _host?.SetSetting(TtsLowLatencySettingName, enabled);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTtsTextNormalization(bool enabled)
    {
        TtsTextNormalization = enabled;
        _host?.SetSetting(TtsTextNormalizationSettingName, enabled);
        _host?.NotifyCapabilitiesChanged();
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    internal static PluginTranscriptionResult ParseSttResponse(string json, string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = GetString(root, "text")?.Trim() ?? "";
        var language = GetString(root, "language");
        var duration = TryGetDouble(root, "duration", out var durationValue) ? durationValue : 0;
        var segments = new List<PluginTranscriptionSegment>();

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("words", out var wordsEl)
            && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                var wordText = GetString(wordEl, "text") ?? "";
                if (string.IsNullOrWhiteSpace(wordText)
                    || !TryGetDouble(wordEl, "start", out var start)
                    || !TryGetDouble(wordEl, "end", out var end))
                {
                    continue;
                }

                segments.Add(new PluginTranscriptionSegment(wordText, start, end));
                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(text, language ?? fallbackLanguage ?? "", duration)
        {
            Segments = segments,
        };
    }

    internal static bool IsLlmModel(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var lowered = id.ToLowerInvariant();
        var excluded = new[] { "stt", "tts", "voice", "image", "embedding" };
        return !excluded.Any(lowered.Contains);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (SelectedLlmModelId is null
            || available.All(model => !string.Equals(model.Id, SelectedLlmModelId, StringComparison.Ordinal)))
        {
            SelectedLlmModelId = available[0].Id;
            if (persist)
                _host?.SetSetting(SelectedLlmModelSettingName, SelectedLlmModelId);
        }
    }

    private void NormalizeSelectedVoice(bool persist)
    {
        var available = AvailableVoices;
        if (available.Count == 0)
            return;

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (_selectedVoiceId is null
            || available.All(voice => !string.Equals(voice.Id, _selectedVoiceId, StringComparison.Ordinal)))
        {
            _selectedVoiceId = available[0].Id;
            if (persist)
                _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        }
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string NormalizeSttModelId(string? modelId) =>
        s_sttModels.Any(model => model.Id == modelId) ? modelId! : DefaultSttModelId;

    private static string? NormalizeVoiceId(string? voiceId) =>
        string.IsNullOrWhiteSpace(voiceId) ? XaiTtsConfiguration.DefaultVoiceId : voiceId.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    private static string NormalizeTtsLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();

    private static List<XaiFetchedModel> NormalizeFetchedLlmModels(IEnumerable<XaiFetchedModel> models) =>
        models
            .Where(model => IsLlmModel(model.Id))
            .DistinctBy(model => model.Id)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<XaiFetchedVoice> NormalizeFetchedVoices(IEnumerable<XaiFetchedVoice> voices) =>
        voices
            .Where(voice => !string.IsNullOrWhiteSpace(voice.VoiceId))
            .DistinctBy(voice => voice.VoiceId)
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // IPluginSettingsProvider
    //
    // The Windows build exposed these via the WPF XaiSettingsView UserControl;
    // the fork renders settings generically from the metadata below. The fork's
    // IPluginSettingsProvider has no explicit "refresh" action, so the dynamic
    // model/voice catalogs are fetched from ValidateAsync (the key-test entry
    // point) and cached via host.SetSetting.

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "xai-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: SelectedModelSettingName,
                Label: Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.TranscriptionModelDescription"),
                Options: s_sttModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: _fetchedLlmModels.Count > 0
                    ? Loc.L("Settings.LlmModelFetched", _fetchedLlmModels.Count)
                    : Loc.L("Settings.LlmModelDefault"),
                Options: SupportedModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: SelectedVoiceSettingName,
                Label: Loc.L("Settings.Voice"),
                Description: _fetchedVoices.Count > 0
                    ? Loc.L("Settings.VoiceFetched", _fetchedVoices.Count)
                    : Loc.L("Settings.VoiceDefault"),
                Options: AvailableVoices
                    .Select(v => new PluginSettingOption(v.Id, v.DisplayName))
                    .ToList()
            ),
            new(
                Key: CustomVoiceIdSettingName,
                Label: Loc.L("Settings.CustomVoiceId"),
                Placeholder: Loc.L("Settings.Optional"),
                Description: Loc.L("Settings.CustomVoiceIdDescription"),
                Kind: PluginSettingKind.Text
            ),
            new(
                Key: TtsLowLatencySettingName,
                Label: Loc.L("Settings.LowLatency"),
                Description: Loc.L("Settings.LowLatencyDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: TtsTextNormalizationSettingName,
                Label: Loc.L("Settings.TextNormalization"),
                Description: Loc.L("Settings.TextNormalizationDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                ApiKeySecretName => ApiKey,
                SelectedModelSettingName => SelectedModelId,
                SelectedLlmModelSettingName => SelectedLlmModelId,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                SelectedVoiceSettingName => _selectedVoiceId,
                CustomVoiceIdSettingName => CustomVoiceId,
                TtsLowLatencySettingName => TtsLowLatency ? "true" : "false",
                TtsTextNormalizationSettingName => TtsTextNormalization ? "true" : "false",
                _ => null,
            }
        );

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case SelectedModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case SelectedLlmModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectLlmModel(value);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
            case SelectedVoiceSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectVoice(value);
                break;
            case CustomVoiceIdSettingName:
                SetCustomVoiceId(value ?? string.Empty);
                break;
            case TtsLowLatencySettingName:
                SetTtsLowLatency(ParseBool(value));
                break;
            case TtsTextNormalizationSettingName:
                SetTtsTextNormalization(ParseBool(value));
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var models = await FetchLlmModelsAsync(ct);
        if (models.Count > 0)
            SetFetchedLlmModels(models);

        var voices = await FetchVoicesAsync(ct);
        if (voices.Count > 0)
            SetFetchedVoices(voices);

        return new PluginSettingsValidationResult(
            true,
            models.Count > 0 || voices.Count > 0
                ? Loc.L("Settings.ApiKeyValidFetched", models.Count, voices.Count)
                : Loc.L("Settings.ApiKeyValidSaved")
        );
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

internal sealed record XaiFetchedModel(string Id, string? OwnedBy);

internal sealed record XaiFetchedVoice(string VoiceId, string? Name, string? Language)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? VoiceId : Name.Trim();
}
