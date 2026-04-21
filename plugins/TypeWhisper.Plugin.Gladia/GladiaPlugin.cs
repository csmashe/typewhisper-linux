using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gladia;

public sealed partial class GladiaPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider
{
    private const string BaseUrl = "https://api.gladia.io/v2";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("default", "Gladia (Auto)"),
    ];

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.gladia";
    public string PluginName => "Gladia";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? Models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "gladia";
    public string ProviderDisplayName => "Gladia";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

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
            throw new InvalidOperationException("Plugin not configured. API key required.");

        // Gladia pre-recorded endpoint with multipart form data
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "audio", "audio.wav");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/pre-recorded");
        request.Headers.Add("x-gladia-key", _apiKey);
        request.Content = content;

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gladia API error {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcript = root
            .GetProperty("result")
            .GetProperty("transcription")
            .GetProperty("full_transcript")
            .GetString() ?? "";

        double duration = 0;
        if (root.GetProperty("result").GetProperty("transcription")
            .TryGetProperty("duration", out var durEl))
            duration = durEl.GetDouble();

        string? detectedLanguage = null;
        if (root.GetProperty("result").GetProperty("transcription")
            .TryGetProperty("languages", out var langsEl)
            && langsEl.ValueKind == JsonValueKind.Array
            && langsEl.GetArrayLength() > 0)
        {
            detectedLanguage = langsEl[0].GetString();
        }

        return new PluginTranscriptionResult(transcript, detectedLanguage, duration, NoSpeechProbability: null);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

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
        new("api-key", "API key", true, null, "Required for Gladia transcription."),
        new(
            "selectedModel",
            "Transcription model",
            Description: "Choose the Gladia model.",
            Options: Models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList())
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            "api-key" => _apiKey,
            "selectedModel" => _selectedModelId,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
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
}
