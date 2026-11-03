// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.mistral.ai";
    private const string ModelId = "voxtral-mini-latest";
    private const string LegacyModelId = "mistral-whisper";

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    public VoxtralPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
    {
    }

    // The plugin takes ownership of the supplied client and disposes it.
    internal VoxtralPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.voxtral";
    public string PluginName => "Voxtral";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        var selectedModelId = host.GetSetting<string>("selectedModel");
        SelectedModelId = selectedModelId == LegacyModelId ? ModelId : selectedModelId ?? ModelId;
        if (selectedModelId == LegacyModelId)
        {
            // A persistence failure must not fail activation; the in-memory migration suffices.
            try
            {
                host.SetSetting("selectedModel", ModelId);
            }
            catch (Exception ex)
            {
                host.Log(PluginLogLevel.Warning, $"Failed to persist model id migration: {ex.Message}");
            }
        }
        ActivateTextRole(host);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        lock (_catalogLock)
        {
            _catalogGeneration++;
            _host = null;
        }

        return Task.CompletedTask;
    }

    public string ProviderId => "voxtral";
    public string ProviderDisplayName => "Voxtral";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [new(ModelId, "Voxtral Mini (Mistral)")];

    public string? SelectedModelId { get; private set; }

    // Mistral documents no OpenAI-style translations endpoint; re-enable only with a documented implementation.
    public bool SupportsTranslation => false;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public void SelectModel(string modelId)
    {
        // Persisted host selections may still carry the legacy id; accept it instead of throwing.
        if (modelId == LegacyModelId)
            modelId = ModelId;
        if (modelId != ModelId)
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
        if (translate)
        {
            throw new InvalidOperationException(
                "Voxtral does not support translation; Mistral only documents the audio transcriptions endpoint."
            );
        }

        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"), PluginRequestFailureKind.Configuration);

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(ModelId), "model");

        // Mistral documents timestamp_granularities as incompatible with language, so an explicit
        // language gets no segments; otherwise ask for them (none are returned unrequested).
        if (!string.IsNullOrWhiteSpace(language))
            content.Add(new StringContent(language), "language");
        else
            content.Add(new StringContent("segment"), "timestamp_granularities");

        // Mistral exposes context_bias as an array; do not guess how a single prompt maps to it.
        _ = prompt;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/v1/audio/transcriptions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"Mistral API error {(int)errorResponse.StatusCode}: {errorBody}");
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        return ParseTranscriptionResponse(responseBody);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "Invalid Mistral transcription response: required field 'text' must be a string."
                );
            }

            var text = textElement.GetString() ?? string.Empty;
            var detectedLanguage =
                root.TryGetProperty("language", out var languageElement)
                && languageElement.ValueKind == JsonValueKind.String
                    ? languageElement.GetString()
                    : null;

            var duration = TryGetPromptAudioSeconds(root, out var promptAudioSeconds)
                ? promptAudioSeconds
                : 0;
            var segments = ParseSegments(root, ref duration);

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
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Invalid Mistral transcription response: the response body is not valid JSON.",
                ex
            );
        }
    }

    private static List<PluginTranscriptionSegment> ParseSegments(
        JsonElement root,
        ref double duration
    )
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("segments", out var segmentsElement)
            || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var segment in segmentsElement.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object
                || !segment.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || !TryGetDouble(segment, "start", out var start)
                || !TryGetDouble(segment, "end", out var end))
            {
                continue;
            }

            segments.Add(
                new PluginTranscriptionSegment(textElement.GetString() ?? string.Empty, start, end)
            );
            duration = Math.Max(duration, end);
        }

        return segments;
    }

    private static bool TryGetPromptAudioSeconds(JsonElement root, out double duration)
    {
        duration = 0;
        return root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && TryGetDouble(usage, "prompt_audio_seconds", out duration);
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

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
        var trimmed = apiKey.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        IPluginHostServices? host;
        // Serialized so an overlapping save cannot restore a key another save already removed.
        await _apiKeyWriteLock.WaitAsync();
        try
        {
            // The saved text catalog is keyed to its owner, so a failed secret write leaves the old pair intact.
            host = _host;
            if (host is not null)
            {
                if (normalized is null)
                    await host.DeleteSecretAsync("api-key");
                else
                    await host.StoreSecretAsync("api-key", normalized);
            }

            if (!string.Equals(ApiKey, normalized, StringComparison.Ordinal))
            {
                lock (_catalogLock)
                {
                    _catalogGeneration++;
                    ApiKey = normalized;
                    SetTextCatalog(host is null ? [] : LoadSavedTextCatalog(host, normalized));
                }
            }
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        host?.NotifyCapabilitiesChanged();
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
                Options: TranscriptionModels
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
                Key: TemperatureModeSettingKey,
                Label: Loc.L("Settings.TemperatureMode"),
                Description: Loc.L("Settings.TemperatureModeDescription"),
                Kind: PluginSettingKind.Dropdown,
                Options:
                [
                    new PluginSettingOption(ProviderDefaultTemperatureMode, Loc.L("Settings.TemperatureProviderDefault")),
                    new PluginSettingOption(CustomTemperatureMode, Loc.L("Settings.TemperatureCustom")),
                ]
            ),
            new(
                Key: TemperatureSettingKey,
                Label: Loc.L("Settings.TemperatureValue"),
                Description: Loc.L("Settings.TemperatureValueDescription"),
                Placeholder: DefaultCustomTemperature.ToString(CultureInfo.InvariantCulture),
                Kind: PluginSettingKind.Text
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                TemperatureModeSettingKey => _temperatureMode,
                TemperatureSettingKey => _temperature.ToString(CultureInfo.InvariantCulture),
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
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
            case TemperatureModeSettingKey:
                SetTemperatureMode(value);
                break;
            case TemperatureSettingKey:
                SetTemperature(value);
                break;
        }
    }

    // Validation doubles as the text model refresh; any failure leaves the saved catalog untouched.
    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        long generation;
        lock (_catalogLock)
            generation = _catalogGeneration;

        List<string> models;
        try
        {
            models = await FetchTextModelsAsync(apiKey, ct);
        }
        catch (PluginRequestException ex) when (ex.FailureKind is PluginRequestFailureKind.Authentication
                                                    or PluginRequestFailureKind.Permission)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
        }
        catch (PluginRequestException ex)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshFailed", ex.Message));
        }

        return TryCommitTextCatalog(models, generation, apiKey)
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValidFetchedTextModels", models.Count))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshStale"));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}
