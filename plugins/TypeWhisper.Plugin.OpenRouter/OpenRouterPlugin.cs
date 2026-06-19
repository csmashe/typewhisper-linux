using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed class OpenRouterPlugin
    : ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://openrouter.ai/api";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedModelsSettingName = "fetchedModels";
    private const string FetchedTranscriptionModelsSettingName = "fetchedTranscriptionModels";
    private const string SelectedTranscriptionModelSettingName = "selectedTranscriptionModel";
    private const string SelectedLlmModelSettingName = "selectedLlmModel";
    private const string UserSelectedLlmModelSettingName = "userSelectedLlmModel";
    private const string TemperatureModeSettingName = "llmTemperatureMode";
    private const string TemperatureValueSettingName = "llmTemperatureValue";
    private const string TemperatureModeProviderDefault = "providerDefault";
    private const string TemperatureModeCustom = "custom";
    internal const string DefaultLlmModelId = "openrouter/free";
    private const string DefaultLlmModelName = "OpenRouter: Free Models Router (free)";
    private const string LegacyFallbackDefaultLlmModelId = "openai/gpt-4o";
    internal const string DefaultTranscriptionModelId = "openai/whisper-large-v3-turbo";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedTranscriptionModelId;
    private string? _selectedLlmModelId;
    private bool _hasUserSelectedLlmModel;
    private string _temperatureMode = TemperatureModeProviderDefault;
    private double _temperatureValue = 0.3;
    private List<OpenRouterFetchedModel> _fetchedTranscriptionModels = [];
    private List<OpenRouterFetchedModel> _fetchedModels = [];
    private bool _streamResponses = true;

    private static readonly IReadOnlyList<PluginModelInfo> FallbackTranscriptionModels =
    [
        new(DefaultTranscriptionModelId, "OpenAI: Whisper Large V3 Turbo") { IsRecommended = true },
        new("openai/whisper-large-v3", "OpenAI: Whisper Large V3"),
        new("openai/whisper-1", "OpenAI: Whisper 1"),
        new("openai/gpt-4o-mini-transcribe", "OpenAI: GPT-4o Mini Transcribe"),
        new("openai/gpt-4o-transcribe", "OpenAI: GPT-4o Transcribe"),
        new("google/chirp-3", "Google: Chirp 3"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackModels =
    [
        new(DefaultLlmModelId, DefaultLlmModelName) { IsRecommended = true },
        new(LegacyFallbackDefaultLlmModelId, "OpenAI: GPT-4o"),
        new("anthropic/claude-sonnet-4", "Anthropic: Claude Sonnet 4"),
        new("google/gemini-2.5-flash-preview", "Google: Gemini 2.5 Flash"),
        new("meta-llama/llama-3.3-70b-instruct", "Meta: Llama 3.3 70B"),
    ];

    private static readonly OpenRouterFetchedModel DefaultFetchedModel =
        new(DefaultLlmModelId, DefaultLlmModelName, "0", "0");

    public OpenRouterPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal OpenRouterPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openrouter";
    public string PluginName => "OpenRouter";
    public string PluginVersion => "1.1.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(
            host.GetSetting<List<OpenRouterFetchedModel>>(FetchedTranscriptionModelsSettingName) ?? []);
        _selectedTranscriptionModelId = host.GetSetting<string>(SelectedTranscriptionModelSettingName);
        _fetchedModels = NormalizeFetchedModels(
            host.GetSetting<List<OpenRouterFetchedModel>>(FetchedModelsSettingName) ?? []);
        _selectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
        _hasUserSelectedLlmModel = host.GetSetting<bool?>(UserSelectedLlmModelSettingName) == true;
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingName));
        _temperatureValue = NormalizeTemperatureValue(host.GetSetting<double?>(TemperatureValueSettingName));
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        NormalizeSelectedTranscriptionModel(persist: true);
        NormalizeSelectedLlmModel(persist: true);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "openrouter";
    public string ProviderDisplayName => "OpenRouter";
    public bool IsConfigured => IsAvailable;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        _fetchedTranscriptionModels.Count > 0
            ? _fetchedTranscriptionModels.Select(model => new PluginModelInfo(model.Id, model.Name)).ToList()
            : FallbackTranscriptionModels;

    public string? SelectedModelId => _selectedTranscriptionModelId;
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (TranscriptionModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            throw new ArgumentException($"Unknown model: {modelId}");

        _selectedTranscriptionModelId = modelId;
        _host?.SetSetting(SelectedTranscriptionModelSettingName, modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("OpenRouter STT does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        var modelId = _selectedTranscriptionModelId ?? TranscriptionModels.First().Id;
        return await SendAudioTranscriptionAsync(modelId, wavAudio, NormalizeLanguage(language), ct);
    }

    // ILlmProviderPlugin

    public string ProviderName => "OpenRouter";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedModels.Count > 0
            ? _fetchedModels.Select(model => new PluginModelInfo(model.Id, model.Name)).ToList()
            : FallbackModels;

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels.First().Id
            : model;

        return await SendChatCompletionAsync(modelId, systemPrompt, userText, ct);
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

        if (!IsAvailable)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels.First().Id
            : model;

        // OpenRouter's batch body emits the same chat.completion shape as the
        // shared helper: always max_tokens 2048, and temperature only in custom
        // mode (provider default otherwise). It sets no extra headers, so the
        // shared streaming helper is a lossless route.
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            temperature: _temperatureMode == TemperatureModeCustom ? _temperatureValue : (double?)null);

        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
    }

    // API key / catalog management

    internal string? ApiKey => _apiKey;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    internal IReadOnlyList<OpenRouterFetchedModel> FetchedTranscriptionModels => _fetchedTranscriptionModels;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<OpenRouterFetchedModel> FetchedModels => _fetchedModels;
    internal string TemperatureMode => _temperatureMode;
    internal double TemperatureValue => _temperatureValue;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasAvailable = IsAvailable;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        _apiKey = normalized;
        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            if (changed && wasAvailable != IsAvailable)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/auth/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
        _hasUserSelectedLlmModel = true;
        _host?.SetSetting(UserSelectedLlmModelSettingName, true);
    }

    internal void SetFetchedModels(List<OpenRouterFetchedModel> models)
    {
        _fetchedModels = NormalizeFetchedModels(models);
        _host?.SetSetting(FetchedModelsSettingName, _fetchedModels);
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetFetchedTranscriptionModels(List<OpenRouterFetchedModel> models)
    {
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(models);
        _host?.SetSetting(FetchedTranscriptionModelsSettingName, _fetchedTranscriptionModels);
        NormalizeSelectedTranscriptionModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetTemperatureMode(string? mode)
    {
        _temperatureMode = NormalizeTemperatureMode(mode);
        _host?.SetSetting(TemperatureModeSettingName, _temperatureMode);
    }

    internal void SetTemperatureValue(double value)
    {
        _temperatureValue = NormalizeTemperatureValue(value);
        _host?.SetSetting(TemperatureValueSettingName, _temperatureValue);
    }

    internal async Task<List<OpenRouterFetchedModel>> FetchModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenRouterModelsResponse>(json, JsonOptions);

            // System.Text.Json happily deserializes `{}` or `{"data": null}`
            // into a record whose non-nullable Data field is null — the
            // catch block below doesn't cover NullReferenceException, so an
            // un-guarded `decoded.Data.Where(...)` would crash the user's
            // Validate click on a degraded/future-shape OpenRouter response
            // instead of falling back to the cached/default catalog.
            var data = decoded?.Data ?? [];

            var models = data
                .Where(model => IsTextLlm(model.Architecture?.Modality, model.Id))
                .Select(model => new OpenRouterFetchedModel(
                    model.Id,
                    string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                    model.Pricing?.Prompt ?? "0",
                    model.Pricing?.Completion ?? "0"))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();

            return NormalizeFetchedModels(models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
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

    internal async Task<List<OpenRouterFetchedModel>> FetchTranscriptionModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v1/models?output_modalities=transcription");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenRouterModelsResponse>(json, JsonOptions);
            var data = decoded?.Data ?? [];

            var models = data
                .Select(model => new OpenRouterFetchedModel(
                    model.Id,
                    string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                    model.Pricing?.Prompt ?? "0",
                    model.Pricing?.Completion ?? "0"))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();

            return NormalizeFetchedTranscriptionModels(models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
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

    internal async Task<double?> FetchCreditsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/auth/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            if (TryReadDouble(data, "limit", out var limit)
                && TryReadDouble(data, "usage", out var usage))
            {
                return limit - usage;
            }

            return TryReadDouble(data, "limit_remaining", out var remaining)
                ? remaining
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool IsTextLlm(string? modality, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var lowered = id.ToLowerInvariant();
        string[] excluded =
        [
            "embed",
            "embedding",
            "tts",
            "audio",
            "image",
            "image-gen",
            "dall-e",
            "stable-diffusion",
            "midjourney",
            "whisper",
            "moderation",
        ];

        if (excluded.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal)))
            return false;

        return string.IsNullOrWhiteSpace(modality)
            || modality.EndsWith("->text", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // Network paths

    private async Task<string> SendChatCompletionAsync(
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            },
            ["max_tokens"] = 2048,
        };

        if (_temperatureMode == TemperatureModeCustom)
            body["temperature"] = _temperatureValue;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    private static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            return content.GetString()?.Trim() ?? "";
        }

        return "";
    }

    private async Task<PluginTranscriptionResult> SendAudioTranscriptionAsync(
        string model,
        byte[] wavAudio,
        string? language,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input_audio"] = new Dictionary<string, string>
            {
                ["data"] = Convert.ToBase64String(wavAudio),
                ["format"] = "wav",
            },
        };

        if (!string.IsNullOrWhiteSpace(language))
            body["language"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    private static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString()?.Trim() ?? ""
            : "";

        var duration = 0.0;
        if (root.TryGetProperty("duration", out var durationElement)
            && durationElement.TryGetDouble(out var rootDuration))
        {
            duration = rootDuration;
        }
        else if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("seconds", out var secondsElement)
            && secondsElement.TryGetDouble(out var usageSeconds))
        {
            duration = usageSeconds;
        }

        // OpenRouter's transcription endpoint echoes the *requested* language
        // (or the model's interpretation of it) rather than a detected ISO code.
        // Drop it to avoid drifting AppSettings.LastDetectedLanguage on every
        // request; explicit language selection happens upstream in the orchestrator.
        return new PluginTranscriptionResult(text, null, duration, null);
    }

    // Normalization helpers

    private void NormalizeSelectedTranscriptionModel(bool persist)
    {
        var available = TranscriptionModels;
        if (available.Count == 0)
            return;

        if (_selectedTranscriptionModelId is not null
            && available.Any(model => string.Equals(model.Id, _selectedTranscriptionModelId, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedTranscriptionModelId = available.First().Id;
        if (persist)
            _host?.SetSetting(SelectedTranscriptionModelSettingName, _selectedTranscriptionModelId);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        // Migrate to openrouter/free *only* when the saved selection is
        // missing or is the legacy pre-1.1.0 default (openai/gpt-4o). A
        // saved non-legacy selection from an older build — including the
        // fork's pre-1.1.0 catalog of anthropic/claude-sonnet-4, google/
        // gemini-2.5-flash, meta-llama/llama-4-scout — is preserved
        // verbatim; those IDs are still valid OpenRouter models and
        // overwriting them would silently downgrade an explicit (often
        // paid) model choice to the free router. (Codex adversarial
        // review caught this — upstream's verbatim version triggered the
        // migration on any saved selection that predated the new
        // userSelectedLlmModel marker.)
        if (string.IsNullOrWhiteSpace(_selectedLlmModelId)
            || string.Equals(_selectedLlmModelId, LegacyFallbackDefaultLlmModelId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedLlmModelId = available.First().Id;
            _hasUserSelectedLlmModel = false;
            if (persist)
            {
                _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
                _host?.SetSetting(UserSelectedLlmModelSettingName, false);
            }
            return;
        }

        // Backfill the user-selection flag for saved selections that
        // predate it. Without this, the next activate would still see
        // _hasUserSelectedLlmModel == false and (under the old guard)
        // re-migrate the preserved value away. The flag's whole purpose
        // is now "the user has expressed a preference at some point".
        if (!_hasUserSelectedLlmModel)
        {
            _hasUserSelectedLlmModel = true;
            if (persist)
                _host?.SetSetting(UserSelectedLlmModelSettingName, true);
        }

        if (_fetchedModels.Count == 0)
            return;

        // A fetched catalog is loaded and the user's saved selection
        // isn't in it (model was removed from OpenRouter, etc.). Fall
        // back to the first available entry but leave the user-selection
        // flag set — the user is still in "I have a preference" mode,
        // we just can't honor their specific pick.
        if (available.Any(model => string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
            return;

        _selectedLlmModelId = available.First().Id;
        if (persist)
            _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
    }

    private static List<OpenRouterFetchedModel> NormalizeFetchedModels(IEnumerable<OpenRouterFetchedModel> models)
    {
        var normalized = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Where(model => !string.Equals(model.Id, DefaultLlmModelId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return [];

        return [DefaultFetchedModel, .. normalized];
    }

    private static List<OpenRouterFetchedModel> NormalizeFetchedTranscriptionModels(IEnumerable<OpenRouterFetchedModel> models) =>
        models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    internal static string NormalizeTemperatureMode(string? mode) =>
        string.Equals(mode, TemperatureModeCustom, StringComparison.OrdinalIgnoreCase)
            ? TemperatureModeCustom
            : TemperatureModeProviderDefault;

    internal static double NormalizeTemperatureValue(double? value)
    {
        // `Math.Clamp(double.NaN, …)` returns NaN unchanged (IEEE 754: every
        // NaN comparison is false, so the min/max checks short-circuit). A
        // persisted NaN would later throw inside System.Text.Json when the
        // chat-completion body is serialized — and re-throw on every activate
        // that loads the setting. Reject non-finite inputs up-front so a
        // corrupted config can't poison the runtime. Matches the OpenAi plugin
        // hardening landed in B5.
        if (value is null || !double.IsFinite(value.Value))
            return 0.3;

        return Math.Clamp(value.Value, 0.0, 2.0);
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
                return true;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(
                    property.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    // IPluginSettingsProvider
    //
    // The Windows build exposed these via a WPF OpenRouterSettingsView UserControl
    // (search box, refresh button, model picker, credits read-out, temperature
    // slider). The fork renders settings generically from the metadata below.
    // The fork has no explicit "refresh" button in the generic settings UI, so
    // ValidateAsync is the user's single entry point: it both validates the key
    // and refreshes the dynamic model catalogs + credits. Same pattern as B1
    // (xAI), B3 (Smallest AI), B4/B5 (OpenAI).

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "sk-or-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: SelectedTranscriptionModelSettingName,
                Label: Loc.L("Settings.TranscriptionModel"),
                Description: _fetchedTranscriptionModels.Count > 0
                    ? Loc.L("Settings.TranscriptionModelFetched", _fetchedTranscriptionModels.Count)
                    : Loc.L("Settings.TranscriptionModelDefault"),
                Options: TranscriptionModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: _fetchedModels.Count > 0
                    ? Loc.L("Settings.LlmModelFetched", _fetchedModels.Count)
                    : Loc.L("Settings.LlmModelDefault"),
                Options: SupportedModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TemperatureModeSettingName,
                Label: Loc.L("Settings.Temperature"),
                Description: Loc.L("Settings.TemperatureModeDescription"),
                Options:
                [
                    new PluginSettingOption(TemperatureModeProviderDefault, Loc.L("Settings.TemperatureProviderDefault")),
                    new PluginSettingOption(TemperatureModeCustom, Loc.L("Settings.TemperatureCustom")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TemperatureValueSettingName,
                Label: Loc.L("Settings.TemperatureValue"),
                Placeholder: "0.3",
                Description: Loc.L("Settings.TemperatureDescription"),
                Kind: PluginSettingKind.Text
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
                ApiKeySecretName => _apiKey,
                SelectedTranscriptionModelSettingName => _selectedTranscriptionModelId,
                SelectedLlmModelSettingName => _selectedLlmModelId,
                TemperatureModeSettingName => _temperatureMode,
                TemperatureValueSettingName => _temperatureValue.ToString(CultureInfo.InvariantCulture),
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                _ => null,
            });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case SelectedTranscriptionModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case SelectedLlmModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectLlmModel(value);
                break;
            case TemperatureModeSettingName:
                SetTemperatureMode(value);
                break;
            case TemperatureValueSettingName:
                // `double.TryParse(..., NumberStyles.Float, ...)` accepts
                // "NaN" / "Infinity" / "-Infinity" — reject them before they
                // can reach the persisted setting (System.Text.Json throws on
                // non-finite doubles by default, which would break both save
                // and the next activate). Matches the OpenAi plugin guard.
                if (double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedTemperature)
                    && double.IsFinite(parsedTemperature))
                {
                    SetTemperatureValue(parsedTemperature);
                }
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var llmModels = await FetchModelsAsync(ct);
        if (llmModels.Count > 0)
            SetFetchedModels(llmModels);

        var transcriptionModels = await FetchTranscriptionModelsAsync(ct);
        if (transcriptionModels.Count > 0)
            SetFetchedTranscriptionModels(transcriptionModels);

        var credits = await FetchCreditsAsync(ct);

        var parts = new List<string> { Loc.L("Settings.ApiKeyValid") };
        if (llmModels.Count > 0)
            parts.Add(Loc.L("Settings.FetchedLlmModels", llmModels.Count));
        if (transcriptionModels.Count > 0)
            parts.Add(Loc.L("Settings.FetchedTranscriptionModels", transcriptionModels.Count));
        if (credits is { } remaining)
        {
            parts.Add(Loc.L(
                "Settings.RemainingCredits",
                FormattableString.Invariant($"${remaining:0.00}")));
        }

        return new PluginSettingsValidationResult(true, string.Join(" ", parts));
    }

    private sealed record OpenRouterModelsResponse(List<OpenRouterApiModel> Data);

    private sealed record OpenRouterApiModel(
        string Id,
        string Name,
        OpenRouterPricing? Pricing,
        OpenRouterArchitecture? Architecture);

    private sealed record OpenRouterPricing(string? Prompt, string? Completion);

    private sealed record OpenRouterArchitecture(string? Modality);
}
