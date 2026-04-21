using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Claude;

public sealed partial class ClaudePlugin : ILlmProviderPlugin, IPluginSettingsProvider
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.claude";
    public string PluginName => "Claude";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ILlmProviderPlugin

    public string ProviderName => "Claude";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("claude-sonnet-4-20250514", "Claude Sonnet 4"),
        new PluginModelInfo("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
    ];

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var requestBody = new
        {
            model,
            max_tokens = 2048,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userText }
            }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _host?.Log(PluginLogLevel.Error, $"Anthropic API error {response.StatusCode}: {responseBody}");
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement.GetProperty("content");
        if (content.GetArrayLength() == 0)
            throw new InvalidOperationException("Anthropic API returned empty content array");

        return content[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Anthropic API returned null text");
    }

    // Internal helpers for settings view

    internal bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;

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

    internal bool ValidateApiKeyFormat(string apiKey)
    {
        return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("sk-ant-");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // IPluginSettingsProvider

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new(
            Key: "api-key",
            Label: "API key",
            IsSecret: true,
            Placeholder: "sk-ant-...",
            Description: "Required for Claude LLM requests.")
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "api-key" ? _apiKey : null);

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (key != "api-key")
            return;

        await SetApiKeyAsync(value ?? string.Empty);
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, "Enter an API key first."));

        var valid = ValidateApiKeyFormat(_apiKey);
        return Task.FromResult<PluginSettingsValidationResult?>(
            valid
                ? new PluginSettingsValidationResult(true, "API key format looks valid.")
                : new PluginSettingsValidationResult(false, "API key format is invalid."));
    }
}
