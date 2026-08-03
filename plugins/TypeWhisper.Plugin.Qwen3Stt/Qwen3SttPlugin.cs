// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

public sealed class Qwen3SttPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string DefaultBaseUrl = "http://localhost:8000";
    private const string DefaultModel = "Qwen/Qwen3-ASR";

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _baseUrl;

    public Qwen3SttPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
    {
    }

    internal Qwen3SttPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

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
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? DefaultModel;
        host.Log(PluginLogLevel.Info, $"Activated (baseUrl={_baseUrl}, configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "qwen3-stt";
    public string ProviderDisplayName => "Qwen3 STT";
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [new("Qwen/Qwen3-ASR", "Qwen3 ASR")];

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public void SelectModel(string modelId)
    {
        if (modelId != DefaultModel)
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
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredBaseUrlRequired"));

        if (translate)
            throw new NotSupportedException(
                "Translation is not supported by the Qwen3 STT plugin."
            );

        var baseUrl = _baseUrl ?? DefaultBaseUrl;
        var apiKey = _apiKey ?? "";
        var model = SelectedModelId ?? DefaultModel;

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            baseUrl,
            apiKey,
            model,
            wavAudio,
            language,
            translate: false,
            "verbose_json",
            ct,
            prompt
        );
    }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

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
        // Strip a trailing /v1 if the user pasted the full endpoint path;
        // the /v1 prefix is added per-request by OpenAiTranscriptionHelper.
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];

        _baseUrl = string.IsNullOrWhiteSpace(normalized) ? DefaultBaseUrl : normalized;
        _host?.SetSetting("baseUrl", _baseUrl);
        _host?.NotifyCapabilitiesChanged();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "baseUrl",
                Loc.L("Settings.BaseUrl"),
                false,
                DefaultBaseUrl,
                Loc.L("Settings.BaseUrlDescription")
            ),
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
                "baseUrl" => _baseUrl,
                "api-key" => _apiKey,
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
