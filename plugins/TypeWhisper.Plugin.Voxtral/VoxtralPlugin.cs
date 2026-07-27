using System.Net.Http;
using System.Net.Http.Headers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.mistral.ai";
    private const string ModelId = "voxtral-mini-latest";
    private const string LegacyModelId = "mistral-whisper";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    public string PluginId => "com.typewhisper.voxtral";
    public string PluginName => "Voxtral";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        var selectedModelId = host.GetSetting<string>("selectedModel");
        _selectedModelId = selectedModelId == LegacyModelId ? ModelId : selectedModelId ?? ModelId;
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
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "voxtral";
    public string ProviderDisplayName => "Voxtral";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [new PluginModelInfo(ModelId, "Voxtral Mini (Mistral)")];

    public string? SelectedModelId => _selectedModelId;
    // Mistral documents no OpenAI-style translations endpoint; re-enable only with a documented implementation.
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        // Persisted host selections may still carry the legacy id; accept it instead of throwing.
        if (modelId == LegacyModelId)
            modelId = ModelId;
        if (modelId != ModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
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
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"));

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            ModelId,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt
        );
    }

    internal string? ApiKey => _apiKey;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey.Trim());

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
                Options: TranscriptionModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => _apiKey,
                "selectedModel" => _selectedModelId,
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
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }

    public void Dispose() => _httpClient.Dispose();
}
