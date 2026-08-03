// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gladia;

public sealed class GladiaPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.gladia.io";

    private static readonly TimeSpan s_defaultPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_defaultPollWindow = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _pollDelay;
    private readonly TimeSpan _pollWindow;
    private IPluginHostServices? _host;
    private string? _apiKey;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new("default", "Gladia (Auto)"),
    ];

    public GladiaPlugin()
        : this(CreateHttpClient())
    {
    }

    internal GladiaPlugin(
        HttpClient httpClient,
        TimeSpan? pollDelay = null,
        TimeSpan? pollWindow = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _pollDelay = pollDelay ?? s_defaultPollDelay;
        _pollWindow = pollWindow ?? s_defaultPollWindow;

        if (_pollDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(pollDelay),
                "The poll delay cannot be negative."
            );

        if (_pollWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(pollWindow),
                "The polling window cannot be negative."
            );
    }

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
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? s_models[0].Id;
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

        return await GladiaStreamingSession.ConnectAsync(_httpClient, _apiKey!, language, ct);
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
        if (translate)
            throw new InvalidOperationException("Gladia does not support translation.");

        // Gladia's pre-recorded request has no prompt equivalent.
        _ = prompt;

        // Snapshot the key so a concurrent settings change cannot alter a multi-request job.
        var apiKey = _apiKey;
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        var audioUrl = await UploadAudioAsync(wavAudio, apiKey, ct);
        var job = await InitiateTranscriptionAsync(audioUrl, language, apiKey, ct);
        var terminalJson = await PollUntilTerminalAsync(job, apiKey, ct);

        try
        {
            using var terminalDocument = ParseProtocolJson(
                terminalJson,
                "polling"
            );
            var terminal = terminalDocument.RootElement;
            var status = RequireString(terminal, "status", "polling");

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Gladia transcription failed: {ExtractProviderDetails(terminal)}"
                );
            }

            return ParseCompletedResult(terminal, NormalizeLanguage(language));
        }
        finally
        {
            await DeleteJobBestEffortAsync(job.Id, apiKey);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<string> UploadAudioAsync(
        byte[] wavAudio,
        string apiKey,
        CancellationToken ct
    )
    {
        using var multipart = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "audio", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/upload");
        AddApiKey(request, apiKey);
        request.Content = multipart;

        var json = await SendJsonAsync(request, "Gladia audio upload", ct);
        using var document = ParseProtocolJson(json, "audio upload");
        return RequireString(document.RootElement, "audio_url", "audio upload");
    }

    private async Task<InitiatedJob> InitiateTranscriptionAsync(
        string audioUrl,
        string? language,
        string apiKey,
        CancellationToken ct
    )
    {
        var payload = new Dictionary<string, object>
        {
            ["audio_url"] = audioUrl,
        };

        if (NormalizeLanguage(language) is { } normalizedLanguage)
        {
            payload["language_config"] = new Dictionary<string, object>
            {
                ["languages"] = new[] { normalizedLanguage },
            };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/v2/pre-recorded"
        );
        AddApiKey(request, apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var json = await SendJsonAsync(request, "Gladia transcription initiation", ct);
        using var document = ParseProtocolJson(json, "transcription initiation");
        var id = RequireString(document.RootElement, "id", "transcription initiation");
        var resultUrl = RequireString(
            document.RootElement,
            "result_url",
            "transcription initiation"
        );

        // Require HTTPS: polling sends x-gladia-key to this URL; non-HTTPS would leak it in plaintext.
        if (!Uri.TryCreate(resultUrl, UriKind.Absolute, out var resultUri)
            || resultUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Gladia transcription initiation response contained an invalid result_url."
            );
        }

        return new InitiatedJob(id, resultUri);
    }

    private async Task<string> PollUntilTerminalAsync(
        InitiatedJob job,
        string apiKey,
        CancellationToken ct
    )
    {
        if (_pollWindow == TimeSpan.Zero)
            throw PollTimeout(job.Id);

        using var timeoutCts = new CancellationTokenSource(_pollWindow);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts.Token
        );

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Get, job.ResultUrl);
                AddApiKey(request, apiKey);

                var json = await SendJsonAsync(
                    request,
                    "Gladia transcription polling",
                    linkedCts.Token
                );
                using var document = ParseProtocolJson(json, "transcription polling");
                var status = RequireString(
                    document.RootElement,
                    "status",
                    "transcription polling"
                );

                switch (status.ToLowerInvariant())
                {
                    case "done":
                    case "error":
                        return json;
                    case "queued":
                    case "processing":
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Gladia transcription polling response contained unknown status '{status}'."
                        );
                }

                if (_pollDelay > TimeSpan.Zero)
                    await Task.Delay(_pollDelay, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw PollTimeout(job.Id);
        }
    }

    private async Task<string> SendJsonAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken ct
    )
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} error {(int)response.StatusCode}: {ExtractProviderDetails(json)}"
            );
        }

        return json;
    }

    private async Task DeleteJobBestEffortAsync(string jobId, string apiKey)
    {
        // Cleanup is best-effort and awaited before returning; bound it short
        // so a stalled DELETE can't hold up the finished result.
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{BaseUrl}/v2/pre-recorded/{Uri.EscapeDataString(jobId)}"
        );
        AddApiKey(request, apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cleanupCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cleanupCts.Token);
                Trace.TraceWarning(
                    "Gladia cleanup could not delete pre-recorded job "
                        + $"{jobId}: {(int)response.StatusCode} {ExtractProviderDetails(responseBody)}"
                );
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"Gladia cleanup could not delete pre-recorded job {jobId}: {ex.Message}"
            );
        }
    }

    private static PluginTranscriptionResult ParseCompletedResult(
        JsonElement root,
        string? fallbackLanguage
    )
    {
        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("transcription", out var transcription)
            || transcription.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Gladia done response did not include result.transcription."
            );
        }

        if (!transcription.TryGetProperty("full_transcript", out var transcriptElement)
            || transcriptElement.ValueKind != JsonValueKind.String
            || transcriptElement.GetString() is not { } transcript)
        {
            throw new InvalidOperationException(
                "Gladia done response did not include a string result.transcription.full_transcript."
            );
        }

        var detectedLanguage = FirstLanguage(transcription);
        var duration = ReadDuration(root, result);
        var segments = ReadSegments(transcription, ref duration, ref detectedLanguage);
        detectedLanguage ??= fallbackLanguage;

        return new PluginTranscriptionResult(
            transcript.Trim(),
            detectedLanguage,
            duration,
            NoSpeechProbability: null
        )
        {
            Segments = segments,
        };
    }

    private static string? FirstLanguage(JsonElement transcription)
    {
        if (!transcription.TryGetProperty("languages", out var languages)
            || languages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- LINQ would box JsonElement's struct enumerator.
        foreach (var language in languages.EnumerateArray())
        {
            if (language.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(language.GetString()))
            {
                return language.GetString();
            }
        }

        return null;
    }

    private static double ReadDuration(JsonElement root, JsonElement result)
    {
        if (result.TryGetProperty("metadata", out var metadata)
            && TryGetDouble(metadata, "audio_duration", out var resultDuration))
        {
            return resultDuration;
        }

        if (root.TryGetProperty("file", out var file)
            && TryGetDouble(file, "audio_duration", out var fileDuration))
        {
            return fileDuration;
        }

        return 0;
    }

    private static List<PluginTranscriptionSegment> ReadSegments(
        JsonElement transcription,
        ref double duration,
        ref string? detectedLanguage
    )
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!transcription.TryGetProperty("utterances", out var utterances)
            || utterances.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var utterance in utterances.EnumerateArray())
        {
            if (utterance.ValueKind != JsonValueKind.Object
                || !TryGetString(utterance, "text", out var text)
                || !TryGetDouble(utterance, "start", out var start)
                || !TryGetDouble(utterance, "end", out var end)
                || end < start)
            {
                continue;
            }

            if (detectedLanguage is null
                && TryGetString(utterance, "language", out var utteranceLanguage)
                && !string.IsNullOrWhiteSpace(utteranceLanguage))
            {
                detectedLanguage = utteranceLanguage;
            }

            segments.Add(new PluginTranscriptionSegment(text, start, end));
            duration = Math.Max(duration, end);
        }

        return segments;
    }

    private static JsonDocument ParseProtocolJson(string json, string operation)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Gladia {operation} response contained invalid JSON.",
                ex
            );
        }
    }

    private static string RequireString(
        JsonElement root,
        string propertyName,
        string operation
    )
    {
        if (root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidOperationException(
            $"Gladia {operation} response did not include a string {propertyName}."
        );
    }

    private static bool TryGetString(
        JsonElement root,
        string propertyName,
        out string value
    )
    {
        if (root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } stringValue)
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetDouble(
        JsonElement root,
        string propertyName,
        out double value
    )
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string ExtractProviderDetails(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "empty response";

        try
        {
            using var document = JsonDocument.Parse(json);
            return ExtractProviderDetails(document.RootElement);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static string ExtractProviderDetails(JsonElement root)
    {
        // TryGetProperty throws on non-objects, so render those bodies directly.
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }

        var details = new List<string>();
        foreach (var propertyName in new[]
                 {
                     "status",
                     "error_code",
                     "error",
                     "error_type",
                     "error_message",
                     "message",
                     "request_id",
                 })
        {
            if (!root.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var rendered = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            details.Add($"{propertyName}={rendered}");
        }

        return details.Count > 0 ? string.Join(", ", details) : root.GetRawText();
    }

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static void AddApiKey(HttpRequestMessage request, string apiKey) =>
        request.Headers.Add("x-gladia-key", apiKey);

    private TimeoutException PollTimeout(string jobId) =>
        new(
            $"Gladia transcription {jobId} did not complete within "
                + $"{_pollWindow.TotalSeconds:0.###} seconds."
        );

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(120),
        };

    private sealed record InitiatedJob(string Id, Uri ResultUrl);

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
        var trimmed = apiKey.Trim();
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
