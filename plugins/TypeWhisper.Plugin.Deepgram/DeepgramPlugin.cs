// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Deepgram;

public sealed class DeepgramPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.deepgram.com";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("nova-3", "Nova-3"),
        new("nova-2", "Nova-2"),
    ];

    public string PluginId => "com.typewhisper.deepgram";
    public string PluginName => "Deepgram";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "deepgram";
    public string ProviderDisplayName => "Deepgram";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    // Every Deepgram model accepts an unspecified language: batch sends
    // detect_language=true and streaming sends language=multi on Nova-3 and
    // defers to the endpoint default otherwise.
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || SelectedModelId is null)
            throw new InvalidOperationException(
                "Plugin not configured. API key and model required."
            );
        return await DeepgramStreamingSession.ConnectAsync(
            ApiKey!,
            SelectedModelId,
            language,
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
        if (!IsConfigured || SelectedModelId is null)
            throw new InvalidOperationException(
                "Plugin not configured. API key and model required."
            );

        var langParam =
            string.IsNullOrEmpty(language)
                ? "&detect_language=true"
                : $"&language={Uri.EscapeDataString(language)}";
        var url =
            $"{BaseUrl}/v1/listen?model={Uri.EscapeDataString(SelectedModelId)}&smart_format=true&punctuate=true{langParam}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", ApiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Deepgram API error {(int)response.StatusCode}: {json}"
            );

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcript =
            root.GetProperty("results")
                .GetProperty("channels")[0]
                .GetProperty("alternatives")[0]
                .GetProperty("transcript")
                .GetString()
            ?? "";

        var duration = root.GetProperty("metadata").GetProperty("duration").GetDouble();

        string? detectedLanguage = null;
        if (
            root.GetProperty("results")
                .GetProperty("channels")[0]
                .TryGetProperty("detected_language", out var langEl)
        )
            detectedLanguage = langEl.GetString();

        return new PluginTranscriptionResult(
            transcript,
            detectedLanguage,
            duration,
            NoSpeechProbability: null
        );
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
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
                "dg...",
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
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
