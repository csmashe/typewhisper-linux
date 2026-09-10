// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed class SpeechmaticsPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://asr.api.speechmatics.com/v2";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private IPluginHostServices? _host;
    private string? _apiKey;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("enhanced", "Speechmatics Enhanced"),
    ];

    public string PluginId => "com.typewhisper.speechmatics";
    public string PluginName => "Speechmatics";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "speechmatics";
    public string ProviderDisplayName => "Speechmatics";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Unsupported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);

        // Defense in depth for direct/legacy callers; the typed host invoker rejects
        // automatic selection before entering the plugin.
        var normalized = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "auto")
            throw new NotSupportedException(
                "Speechmatics does not support automatic language detection. Choose an explicit language for this profile."
            );

        return await SpeechmaticsStreamingSession.ConnectAsync(_apiKey!, normalized, ct);
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
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);

        // Defense in depth for direct/legacy callers; the typed host invoker rejects
        // automatic selection before entering the plugin.
        var normalized = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "auto")
            throw new NotSupportedException(
                "Speechmatics does not support automatic language detection. Choose an explicit language for this profile."
            );

        var config = JsonSerializer.Serialize(
            new
            {
                type = "transcription",
                transcription_config = new { language = normalized, operating_point = "enhanced" },
            }
        );

        using var submitContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        submitContent.Add(fileContent, "data_file", "audio.wav");
        submitContent.Add(new StringContent(config, Encoding.UTF8, "application/json"), "config");

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/jobs");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        submitRequest.Content = submitContent;

        using var submitResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, submitRequest, HttpCompletionOption.ResponseContentRead, ct,
            // Status + reason only: the body can echo upload metadata and partial transcripts.
            (errorResponse, _) =>
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Speechmatics submit error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase})"
                );
                return $"Speechmatics API error {(int)errorResponse.StatusCode}: {errorResponse.ReasonPhrase}";
            });
        var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);

        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId =
            submitDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No job ID in Speechmatics response");

        var transcript = await PollForTranscriptAsync(jobId, ct);
        return transcript;
    }

    private async Task<PluginTranscriptionResult> PollForTranscriptAsync(
        string jobId,
        CancellationToken ct
    )
    {
        const int maxAttempts = 120;
        const int delayMs = 2000;

        for (var i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delayMs, ct);

            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/jobs/{jobId}"
            );
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var statusResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, statusRequest, HttpCompletionOption.ResponseContentRead, ct,
                // Status + reason only: the body can echo upload metadata and partial transcripts.
                (errorResponse, _) =>
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Speechmatics status error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase}) for job {jobId}"
                    );
                    return $"Speechmatics status error {(int)errorResponse.StatusCode} for job {jobId}: {errorResponse.ReasonPhrase}";
                });
            var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);

            using var statusDoc = JsonDocument.Parse(statusJson);
            var job = statusDoc.RootElement.GetProperty("job");
            var status = job.GetProperty("status").GetString();

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- subjective control-flow style; the if-chain reads fine here.
            if (status == "done")
            {
                using var transcriptRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{BaseUrl}/jobs/{jobId}/transcript?format=json-v2"
                );
                transcriptRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _apiKey
                );

                using var transcriptResponse = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                    _httpClient, transcriptRequest, HttpCompletionOption.ResponseContentRead, ct,
                    // Status + reason only: the body can echo upload metadata and partial transcripts.
                    (errorResponse, _) =>
                    {
                        _host?.Log(
                            PluginLogLevel.Warning,
                            $"Speechmatics transcript error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase}) for job {jobId}"
                        );
                        return $"Speechmatics transcript error {(int)errorResponse.StatusCode} for job {jobId}: {errorResponse.ReasonPhrase}";
                    });
                var transcriptJson = await transcriptResponse.Content.ReadAsStringAsync(ct);

                return ParseTranscript(transcriptJson, job);
            }

            // ReSharper disable once MergeIntoLogicalPattern -- subjective style; kept as-is.
            if (status == "rejected" || status == "deleted")
                throw new InvalidOperationException($"Speechmatics job {jobId} {status}");
        }

        throw new TimeoutException(
            $"Speechmatics job {jobId} did not complete within {maxAttempts * delayMs / 1000}s"
        );
    }

    private static PluginTranscriptionResult ParseTranscript(string json, JsonElement job)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (
            root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var result in results.EnumerateArray())
            {
                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                if (
                    result.TryGetProperty("alternatives", out var alts)
                    && alts.ValueKind == JsonValueKind.Array
                    && alts.GetArrayLength() > 0
                )
                {
                    var content = alts[0].GetProperty("content").GetString();
                    if (!string.IsNullOrEmpty(content))
                        sb.Append(content);
                }
            }
        }

        double duration = 0;
        if (job.TryGetProperty("duration", out var durEl))
            duration = durEl.GetDouble();

        string? detectedLanguage = null;
        if (
            root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("language", out var langEl)
        )
        {
            detectedLanguage = langEl.GetString();
        }

        return new PluginTranscriptionResult(
            sb.ToString().Trim(),
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
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
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
