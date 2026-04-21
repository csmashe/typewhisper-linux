using System.Net.Http;
using System.Net.Http.Headers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

public sealed partial class Qwen3SttPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider
{
    private const string DefaultBaseUrl = "http://localhost:8000";
    private const string DefaultModel = "Qwen/Qwen3-ASR";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _baseUrl;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.qwen3-stt";
    public string PluginName => "Qwen3 STT";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _baseUrl = host.GetSetting<string>("baseUrl");
        if (string.IsNullOrWhiteSpace(_baseUrl))
            _baseUrl = DefaultBaseUrl;
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? DefaultModel;
        host.Log(PluginLogLevel.Info, $"Activated (baseUrl={_baseUrl}, configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "qwen3-stt";
    public string ProviderDisplayName => "Qwen3 STT";
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new PluginModelInfo("Qwen/Qwen3-ASR", "Qwen3 ASR")];

    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (modelId != DefaultModel)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. Base URL required.");

        var baseUrl = _baseUrl ?? DefaultBaseUrl;
        var apiKey = _apiKey ?? "";
        var model = _selectedModelId ?? DefaultModel;

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient, baseUrl, apiKey, model,
            wavAudio, language, translate: false, "verbose_json", ct);
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

    internal void SetBaseUrl(string url)
    {
        var normalized = url.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];

        _baseUrl = string.IsNullOrWhiteSpace(normalized) ? DefaultBaseUrl : normalized;
        _host?.SetSetting("baseUrl", _baseUrl);
        _host?.NotifyCapabilitiesChanged();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new("baseUrl", "Base URL", false, DefaultBaseUrl, "OpenAI-compatible server base URL."),
        new("api-key", "API key", true, null, "Optional bearer token for the server."),
        new(
            "selectedModel",
            "Transcription model",
            Description: "Choose the Qwen3 STT model.",
            Options: TranscriptionModels.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList())
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            "baseUrl" => _baseUrl,
            "api-key" => _apiKey,
            "selectedModel" => _selectedModelId,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case "baseUrl":
                SetBaseUrl(value ?? DefaultBaseUrl);
                break;
            case "api-key":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
