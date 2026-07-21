// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed class CloudflareAsrPlugin
    : ITranscriptionEnginePlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiToken;
    private string? _accountId;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("whisper", "Whisper (Cloudflare)"),
    ];

    public string PluginId => "com.typewhisper.cloudflare-asr";
    public string PluginName => "Cloudflare ASR";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        // Normalize on load: legacy values saved before SetApiTokenAsync trimmed
        // could otherwise reach the Bearer header with trailing whitespace and
        // 401 every request while IsConfigured still reports true.
        var loadedToken = await host.LoadSecretAsync("api-token");
        _apiToken = string.IsNullOrWhiteSpace(loadedToken) ? null : loadedToken.Trim();
        var loadedAccount = await host.LoadSecretAsync("account-id");
        _accountId = string.IsNullOrWhiteSpace(loadedAccount) ? null : loadedAccount.Trim();
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "cloudflare-asr";
    public string ProviderDisplayName => "Cloudflare ASR";
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_apiToken) && !string.IsNullOrEmpty(_accountId);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;

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
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Plugin not configured. Account ID and API token required."
            );

        var url =
            $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/ai/run/@cf/openai/whisper";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Keep only the stable HTTP status + reason in both the plugin log
            // and the thrown exception; the raw response body may echo request
            // fragments or token-bearing identifiers that we don't want in any
            // diagnostic surface.
            _host?.Log(
                PluginLogLevel.Warning,
                $"Cloudflare API error {(int)response.StatusCode} ({response.ReasonPhrase})"
            );
            throw new HttpRequestException(
                $"Cloudflare API error {(int)response.StatusCode}: {response.ReasonPhrase}"
            );
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        if (
            root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("text", out var textEl)
        )
        {
            text = textEl.GetString() ?? "";
        }

        // Language and duration are nested under result.language / result.duration;
        // both fields are optional and absent when Cloudflare can't determine them.
        string? detectedLanguage = null;
        if (
            root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("language", out var langEl)
        )
        {
            detectedLanguage = langEl.GetString();
        }

        double duration = 0;
        if (
            root.TryGetProperty("result", out var res2)
            && res2.ValueKind == JsonValueKind.Object
            && res2.TryGetProperty("duration", out var durEl)
            && durEl.ValueKind == JsonValueKind.Number
            && durEl.TryGetDouble(out var parsedDuration)
        )
        {
            duration = parsedDuration;
        }

        return new PluginTranscriptionResult(
            text.Trim(),
            detectedLanguage,
            duration,
            NoSpeechProbability: null
        );
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

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
        var trimmed = apiToken.Trim();
        _apiToken = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-token");
            else
                await _host.StoreSecretAsync("api-token", trimmed);

            _host.NotifyCapabilitiesChanged();
        }
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "account-id",
                Loc.L("Settings.AccountId"),
                false,
                null,
                Loc.L("Settings.AccountIdDescription")
            ),
            new(
                "api-token",
                Loc.L("Settings.ApiToken"),
                true,
                null,
                Loc.L("Settings.ApiTokenDescription")
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
                "account-id" => _accountId,
                "api-token" => _apiToken,
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
                new PluginSettingsValidationResult(
                    false,
                    Loc.L("Settings.EnterAccountIdAndApiToken")
                )
            );

        return Task.FromResult<PluginSettingsValidationResult?>(
            new PluginSettingsValidationResult(true, Loc.L("Settings.CredentialsSaved"))
        );
    }
}
