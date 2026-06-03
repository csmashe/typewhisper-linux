using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GoogleCloudStt;

// NOTE (2026-05-29): This plugin is intentionally BATCH-ONLY for now — it does not
// implement real-time streaming (no SupportsStreaming override). Other cloud STT
// providers here stream over a WebSocket reusing their existing API key, but Google
// has no REST/WebSocket real-time API: StreamingRecognize is gRPC-only. Adding it
// would mean a heavy new gRPC + protobuf dependency (with AOT-trim risk) AND
// migrating auth from the plain API key used below to a service-account / ADC
// credential. That is a research spike, not a drop-in change, so it is parked:
// leaving the plugin as-is until that cost is justified or a streaming reference
// to follow exists.
public sealed partial class GoogleCloudSttPlugin
    : ITranscriptionEnginePlugin,
        IPluginSettingsProvider
{
    private const string ApiEndpoint = "https://speech.googleapis.com/v1/speech:recognize";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    public string PluginId => "com.typewhisper.google-cloud-stt";
    public string PluginName => "Google Cloud STT";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? "latest_long";
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "google-cloud-stt";
    public string ProviderDisplayName => "Google Cloud";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [new PluginModelInfo("latest_long", "Google Cloud (Long)")];

    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (modelId != "latest_long")
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

        // Google STT's LINEAR16 encoding wants raw PCM, not a WAV container —
        // strip the standard 44-byte header. (TypeWhisper always emits the
        // simple PCM-WAV layout, so the fixed offset is safe here.)
        var pcmData = wavAudio.Length > 44 ? wavAudio[44..] : wavAudio;
        var audioBase64 = Convert.ToBase64String(pcmData);

        var langCode = !string.IsNullOrEmpty(language) && language != "auto" ? language : "en-US";
        // Google requires BCP-47; the rest of the app uses ISO-639-1 ("en"),
        // so expand 2-letter codes to a regional variant before sending.
        if (langCode.Length == 2)
            langCode = MapToGoogleLanguageCode(langCode);

        var requestBody = new
        {
            config = new
            {
                encoding = "LINEAR16",
                sampleRateHertz = 16000,
                languageCode = langCode,
                model = "latest_long",
            },
            audio = new { content = audioBase64 },
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}?key={_apiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(responseJson, langCode);
    }

    private static PluginTranscriptionResult ParseResponse(string json, string requestedLanguage)
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
                    result.TryGetProperty("alternatives", out var alternatives)
                    && alternatives.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var alt in alternatives.EnumerateArray())
                    {
                        if (alt.TryGetProperty("transcript", out var transcript))
                        {
                            if (sb.Length > 0)
                                sb.Append(' ');
                            sb.Append(transcript.GetString());
                        }
                    }
                }
            }
        }

        // v1 has no audio_duration field; totalBilledTime ("15s" / "15.500s")
        // is the closest proxy. Falls back to 0 when absent.
        double duration = 0;
        if (root.TryGetProperty("totalBilledTime", out var billedTime))
        {
            var billedStr = billedTime.GetString() ?? "";
            if (
                billedStr.EndsWith("s")
                && double.TryParse(
                    billedStr[..^1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var secs
                )
            )
            {
                duration = secs;
            }
        }

        string? detectedLang = null;
        if (
            root.TryGetProperty("results", out var resultsForLang)
            && resultsForLang.ValueKind == JsonValueKind.Array
            && resultsForLang.GetArrayLength() > 0
        )
        {
            var first = resultsForLang[0];
            if (first.TryGetProperty("languageCode", out var lc))
                detectedLang = lc.GetString();
        }

        return new PluginTranscriptionResult(
            sb.ToString().Trim(),
            detectedLang ?? requestedLanguage,
            duration
        );
    }

    private static string MapToGoogleLanguageCode(string iso) =>
        iso.ToLowerInvariant() switch
        {
            "en" => "en-US",
            "de" => "de-DE",
            "fr" => "fr-FR",
            "es" => "es-ES",
            "it" => "it-IT",
            "pt" => "pt-BR",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "zh" => "zh-CN",
            "ru" => "ru-RU",
            "nl" => "nl-NL",
            "pl" => "pl-PL",
            "sv" => "sv-SE",
            "da" => "da-DK",
            "fi" => "fi-FI",
            "no" => "nb-NO",
            "tr" => "tr-TR",
            "ar" => "ar-SA",
            "hi" => "hi-IN",
            "uk" => "uk-UA",
            "cs" => "cs-CZ",
            _ => iso,
        };

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

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new("api-key", "API key", true, null, "Required for Google Cloud Speech-to-Text."),
            new(
                "selectedModel",
                "Transcription model",
                Description: "Choose the Google Cloud STT model.",
                Options: TranscriptionModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
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

    public void Dispose() => _httpClient.Dispose();
}
