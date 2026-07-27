using System.Net.Http;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gladia;

public sealed partial class GladiaPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("default", "Gladia (Auto)"),
    ];

    public string PluginId => "com.typewhisper.gladia";
    public string PluginName => "Gladia";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        // Trim on load to mirror SetApiKeyAsync: legacy secrets saved before
        // the save-side trim could otherwise leave the x-gladia-key header
        // with trailing whitespace while IsConfigured still reports true.
        var loaded = await host.LoadSecretAsync("api-key");
        _apiKey = string.IsNullOrWhiteSpace(loaded) ? null : loaded.Trim();
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? Models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "gladia";
    public string ProviderDisplayName => "Gladia";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => true;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        return await GladiaStreamingSession.ConnectAsync(_httpClient, _apiKey!, language, ct);
    }

    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    // Batch intentionally throws until Gladia's upload/initiate/poll protocol is implemented.
    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        throw new NotSupportedException(
            "Gladia batch transcription is not supported in this build; use live streaming. "
                + "The batch API requires a multi-stage upload/poll protocol that is not yet implemented."
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

    internal async Task SetApiKeyAsync(string apiKey)
    {
        // Trim defensively at the internal entry too: SetSettingValueAsync
        // already trims, but a future direct caller could re-introduce
        // trailing whitespace that breaks the x-gladia-key header.
        var trimmed = apiKey?.Trim();
        _apiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", trimmed);

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
                Options: Models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
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
                // Normalize whitespace once — pasted keys often pick up
                // trailing newlines or spaces that break the request header.
                await SetApiKeyAsync(value?.Trim() ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }
}
