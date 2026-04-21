using System.Net.Http;
using System.Net.Http.Headers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Fireworks;

public sealed partial class FireworksPlugin : ILlmProviderPlugin, IDisposable, IPluginSettingsProvider
{
    private const string BaseUrl = "https://api.fireworks.ai";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private IPluginHostServices? _host;
    private string? _apiKey;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.fireworks";
    public string PluginName => "Fireworks";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("apiKey");
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ILlmProviderPlugin

    public string ProviderName => "Fireworks";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("accounts/fireworks/models/llama4-scout-instruct-basic", "Llama 4 Scout") { IsRecommended = true },
    ];

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("API key not configured");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, model, systemPrompt, userText, ct);
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("apiKey");
            else
                await _host.StoreSecretAsync("apiKey", apiKey);

            _host.NotifyCapabilitiesChanged();
        }
    }

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

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new(
            Key: "apiKey",
            Label: "API key",
            IsSecret: true,
            Placeholder: "fw-...",
            Description: "Required for Fireworks LLM requests.")
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "apiKey" ? _apiKey : null);

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (key != "apiKey")
            return;

        await SetApiKeyAsync(value ?? string.Empty);
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, "Enter an API key first.");

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, "API key is valid.")
            : new PluginSettingsValidationResult(false, "API key is invalid.");
    }

    public void Dispose() => _httpClient.Dispose();
}
