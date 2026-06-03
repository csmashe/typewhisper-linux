using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed partial class SpeechmaticsPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider
{
    private const string BaseUrl = "https://asr.api.speechmatics.com/v2";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    private static readonly IReadOnlyList<PluginModelInfo> Models =
    [
        new("enhanced", "Speechmatics Enhanced"),
    ];

    public string PluginId => "com.typewhisper.speechmatics";
    public string PluginName => "Speechmatics";
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

    public string ProviderId => "speechmatics";
    public string ProviderDisplayName => "Speechmatics";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Models;

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => true;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        // Speechmatics v2 requires an explicit language code; it has no automatic
        // language detection. The host maps an "auto" profile to null before calling
        // here, so reject null/empty/"auto" rather than silently streaming as English
        // (which produces garbage for non-English audio). Mirrors the batch
        // TranscribeAsync guard; throwing makes the host fall back to batch, which
        // applies the same guard and surfaces a clear error.
        var normalized = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "auto")
            throw new NotSupportedException(
                "Speechmatics does not support automatic language detection. Choose an explicit language for this profile."
            );

        return await SpeechmaticsStreamingSession.ConnectAsync(_apiKey!, normalized, ct);
    }

    public void SelectModel(string modelId)
    {
        if (Models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
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
            throw new InvalidOperationException("Plugin not configured. API key required.");

        // Speechmatics v2 requires an explicit language code; it has no automatic
        // language detection. Reject null/empty/"auto" rather than silently
        // transcribing as English, which produces garbage output for non-English
        // audio. Normalize first so " Auto " / "AUTO" / etc. from less-careful
        // callers hit the same guard. Mirrors the StartStreamingAsync guard so the
        // streaming→batch fallback path surfaces the same clear error.
        var normalized = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "auto")
            throw new NotSupportedException(
                "Speechmatics does not support automatic language detection. Choose an explicit language for this profile."
            );

        var lang = normalized;

        var config = JsonSerializer.Serialize(
            new
            {
                type = "transcription",
                transcription_config = new { language = lang, operating_point = "enhanced" },
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

        using var submitResponse = await _httpClient.SendAsync(submitRequest, ct);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);

        if (!submitResponse.IsSuccessStatusCode)
        {
            // Log only the stable HTTP status; the response body can echo
            // upload metadata (and on retries, partial transcripts) which
            // we don't want persisted in the plugin log.
            _host?.Log(
                PluginLogLevel.Warning,
                $"Speechmatics submit error {(int)submitResponse.StatusCode} ({submitResponse.ReasonPhrase})"
            );
            throw new HttpRequestException(
                $"Speechmatics API error {(int)submitResponse.StatusCode}: {submitResponse.ReasonPhrase}"
            );
        }

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

            using var statusResponse = await _httpClient.SendAsync(statusRequest, ct);
            var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);

            if (!statusResponse.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Speechmatics status error {(int)statusResponse.StatusCode} ({statusResponse.ReasonPhrase}) for job {jobId}"
                );
                throw new HttpRequestException(
                    $"Speechmatics status error {(int)statusResponse.StatusCode} for job {jobId}: {statusResponse.ReasonPhrase}"
                );
            }

            using var statusDoc = JsonDocument.Parse(statusJson);
            var job = statusDoc.RootElement.GetProperty("job");
            var status = job.GetProperty("status").GetString();

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

                using var transcriptResponse = await _httpClient.SendAsync(transcriptRequest, ct);
                var transcriptJson = await transcriptResponse.Content.ReadAsStringAsync(ct);

                if (!transcriptResponse.IsSuccessStatusCode)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"Speechmatics transcript error {(int)transcriptResponse.StatusCode} ({transcriptResponse.ReasonPhrase}) for job {jobId}"
                    );
                    throw new HttpRequestException(
                        $"Speechmatics transcript error {(int)transcriptResponse.StatusCode} for job {jobId}: {transcriptResponse.ReasonPhrase}"
                    );
                }

                return ParseTranscript(transcriptJson, job);
            }

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
            new("api-key", "API key", true, null, "Required for Speechmatics transcription."),
            new(
                "selectedModel",
                "Transcription model",
                Description: "Choose the Speechmatics model.",
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
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }
}
