using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GoogleCloudStt;

public sealed partial class GoogleCloudSttPlugin : ITranscriptionEnginePlugin, IPluginSettingsProvider
{
    private const string ApiEndpoint = "https://speech.googleapis.com/v1/speech:recognize";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;

    // ITypeWhisperPlugin

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

    // ITranscriptionEnginePlugin

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
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Plugin not configured. API key required.");

        // Strip WAV header (first 44 bytes) to get raw PCM16 data
        var pcmData = wavAudio.Length > 44 ? wavAudio[44..] : wavAudio;
        var audioBase64 = Convert.ToBase64String(pcmData);

        var langCode = !string.IsNullOrEmpty(language) && language != "auto" ? language : "en-US";
        // Google expects BCP-47 codes; convert short ISO codes (e.g. "en" -> "en-US", "de" -> "de-DE")
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
            audio = new
            {
                content = audioBase64,
            },
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

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("alternatives", out var alternatives)
                    && alternatives.ValueKind == JsonValueKind.Array)
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

        // Google STT v1 does not return audio duration in the response;
        // extract from totalBilledTime if available, otherwise default to 0.
        double duration = 0;
        if (root.TryGetProperty("totalBilledTime", out var billedTime))
        {
            var billedStr = billedTime.GetString() ?? "";
            // Format: "15s" or "15.500s"
            if (billedStr.EndsWith("s") &&
                double.TryParse(billedStr[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs))
            {
                duration = secs;
            }
        }

        // Extract language from the first result's languageCode if present
        string? detectedLang = null;
        if (root.TryGetProperty("results", out var resultsForLang)
            && resultsForLang.ValueKind == JsonValueKind.Array
            && resultsForLang.GetArrayLength() > 0)
        {
            var first = resultsForLang[0];
            if (first.TryGetProperty("languageCode", out var lc))
                detectedLang = lc.GetString();
        }

        return new PluginTranscriptionResult(
            sb.ToString().Trim(),
            detectedLang ?? requestedLanguage,
            duration);
    }

    private static string MapToGoogleLanguageCode(string iso) => iso.ToLowerInvariant() switch
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
            Options: TranscriptionModels.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList())
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            "api-key" => _apiKey,
            "selectedModel" => _selectedModelId,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
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
