// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed class AssemblyAiPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.assemblyai.com";

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("universal-3-pro", "Universal-3 Pro"),
        new("universal-2", "Universal-2"),
    ];

    public string PluginId => "com.typewhisper.assemblyai";
    public string PluginName => "AssemblyAI";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "assemblyai";
    public string ProviderDisplayName => "AssemblyAI";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));
        return await AssemblyAiStreamingSession.ConnectAsync(ApiKey!, language, ct);
    }

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
        if (!IsConfigured || SelectedModelId is null)
            throw new InvalidOperationException(
                "Plugin not configured. API key and model required."
            );

        var uploadUrl = await UploadAudioAsync(wavAudio, ct);
        var transcriptId = await SubmitTranscriptionAsync(uploadUrl, language, ct);
        return await PollForResultAsync(transcriptId, ct);
    }

    private async Task<string> UploadAudioAsync(byte[] wavAudio, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/upload");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AssemblyAI upload error {(int)response.StatusCode}: {json}"
            );

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("upload_url").GetString()
            ?? throw new InvalidOperationException("Missing upload_url in response");
    }

    private async Task<string> SubmitTranscriptionAsync(
        string audioUrl,
        string? language,
        CancellationToken ct
    )
    {
        var body = new Dictionary<string, object>
        {
            ["audio_url"] = audioUrl,
            ["speech_models"] = new[] { SelectedModelId! },
        };

        if (string.IsNullOrEmpty(language))
            body["language_detection"] = true;
        else
            body["language_code"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/transcript");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AssemblyAI submit error {(int)response.StatusCode}: {json}"
            );

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Missing id in response");
    }

    private async Task<PluginTranscriptionResult> PollForResultAsync(
        string transcriptId,
        CancellationToken ct
    )
    {
        for (var i = 0; i < 300; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/v2/transcript/{transcriptId}"
            );
            request.Headers.Add("Authorization", ApiKey);

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"AssemblyAI poll error {(int)response.StatusCode}: {json}"
                );

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- subjective control-flow style; the if-chain reads fine here.
            if (status == "error")
            {
                var error = root.TryGetProperty("error", out var errEl)
                    ? errEl.GetString()
                    : "Unknown error";
                throw new InvalidOperationException($"AssemblyAI transcription failed: {error}");
            }

            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (status == "completed")
            {
                var text = root.GetProperty("text").GetString() ?? "";
                var duration = root.TryGetProperty("audio_duration", out var durEl)
                    ? durEl.GetDouble()
                    : 0.0;
                var detectedLanguage = root.TryGetProperty("language_code", out var langEl)
                    ? langEl.GetString()
                    : null;
                return new PluginTranscriptionResult(
                    text,
                    detectedLanguage,
                    duration,
                    NoSpeechProbability: null
                );
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out after 5 minutes");
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
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v2/transcript?limit=1"
        );
        request.Headers.Add("Authorization", apiKey);
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
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                "aa...",
                Loc.L("Settings.ApiKeyDescription")
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
                "api-key" => ApiKey,
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
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }
}
