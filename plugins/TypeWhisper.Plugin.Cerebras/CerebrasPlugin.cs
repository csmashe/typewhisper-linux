// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cerebras;

public sealed class CerebrasPlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.cerebras.ai";

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private bool _streamResponses = true;

    public CerebrasPlugin()
        : this(new HttpClient())
    {
    }

    internal CerebrasPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.cerebras";
    public string PluginName => "Cerebras";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderName => "Cerebras";
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [new("llama-4-scout-17b-16e-instruct", "Llama 4 Scout 17B")];

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            model,
            systemPrompt,
            userText,
            ct
        );
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!_streamResponses)
        {
            yield return await ProcessAsync(systemPrompt, userText, model, ct);
            yield break;
        }

        if (!IsAvailable)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            model,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "api-key",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "csk-...",
                Description: Loc.L("Settings.ApiKeyDescription")
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
                "api-key" => ApiKey,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
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
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }
}
