using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed partial class CloudflareAsrPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiToken;
    private string? _accountId;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("whisper", "Whisper (Cloudflare)"),
    ];

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.cloudflare-asr";
    public string PluginName => "Cloudflare ASR";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiToken = await host.LoadSecretAsync("api-token");
        _accountId = await host.LoadSecretAsync("account-id");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? Models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "cloudflare-asr";
    public string ProviderDisplayName => "Cloudflare ASR";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiToken) && !string.IsNullOrEmpty(_accountId);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Account ID and API token required.");

        var url = $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/ai/run/@cf/openai/whisper";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloudflare API error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        if (root.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("text", out var textEl))
                text = textEl.GetString() ?? "";
        }

        // Cloudflare Workers AI Whisper doesn't return duration or language detection
        // in the standard response, so we use defaults
        string? detectedLanguage = null;
        if (root.TryGetProperty("result", out var res)
            && res.TryGetProperty("language", out var langEl))
        {
            detectedLanguage = langEl.GetString();
        }

        double duration = 0;
        if (root.TryGetProperty("result", out var res2)
            && res2.TryGetProperty("duration", out var durEl))
        {
            duration = durEl.GetDouble();
        }

        return new PluginTranscriptionResult(text.Trim(), detectedLanguage, duration, NoSpeechProbability: null);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    internal async Task SetAccountIdAsync(string accountId)
    {
        _accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                await _host.DeleteSecretAsync("account-id");
            else
                await _host.StoreSecretAsync("account-id", accountId.Trim());

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task SetApiTokenAsync(string apiToken)
    {
        _apiToken = string.IsNullOrWhiteSpace(apiToken) ? null : apiToken;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                await _host.DeleteSecretAsync("api-token");
            else
                await _host.StoreSecretAsync("api-token", apiToken);

            _host.NotifyCapabilitiesChanged();
        }
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new("account-id", "Account ID", false, null, "Required Cloudflare account identifier."),
        new("api-token", "API token", true, null, "Cloudflare API token with Workers AI access."),
        new(
            "selectedModel",
            "Transcription model",
            Description: "Choose the Cloudflare ASR model.",
            Options: Models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList())
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            "account-id" => _accountId,
            "api-token" => _apiToken,
            "selectedModel" => _selectedModelId,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case "account-id":
                await SetAccountIdAsync(value ?? string.Empty);
                break;
            case "api-token":
                await SetApiTokenAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_accountId) || string.IsNullOrWhiteSpace(_apiToken))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, "Enter both Account ID and API token first."));

        return Task.FromResult<PluginSettingsValidationResult?>(
            new PluginSettingsValidationResult(true, "Credentials saved. Remote validation is not implemented yet."));
    }
}
