// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

public sealed class ElevenLabsPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    internal const string DefaultModelId = "scribe_v2";
    private const string BaseUrl = "https://api.elevenlabs.io";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";

    private static readonly SearchValues<char> s_invalidKeytermCharacters = SearchValues.Create("<>{}[]\\");

    private static readonly IReadOnlyList<ElevenLabsModelEntry> s_modelEntries =
    [
        new(DefaultModelId, "Scribe v2", "scribe_v2", "scribe_v2_realtime"),
    ];

    private static readonly IReadOnlyList<string> s_languages =
    [
        "af",
        "am",
        "ar",
        "as",
        "az",
        "ba",
        "be",
        "bg",
        "bn",
        "bo",
        "br",
        "bs",
        "ca",
        "cs",
        "cy",
        "da",
        "de",
        "el",
        "en",
        "es",
        "et",
        "eu",
        "fa",
        "fi",
        "fo",
        "fr",
        "gl",
        "gu",
        "ha",
        "haw",
        "he",
        "hi",
        "hr",
        "ht",
        "hu",
        "hy",
        "id",
        "is",
        "it",
        "ja",
        "jw",
        "ka",
        "kk",
        "km",
        "kn",
        "ko",
        "la",
        "lb",
        "ln",
        "lo",
        "lt",
        "lv",
        "mg",
        "mi",
        "mk",
        "ml",
        "mn",
        "mr",
        "ms",
        "mt",
        "my",
        "ne",
        "nl",
        "nn",
        "no",
        "oc",
        "pa",
        "pl",
        "ps",
        "pt",
        "ro",
        "ru",
        "sa",
        "sd",
        "si",
        "sk",
        "sl",
        "sn",
        "so",
        "sq",
        "sr",
        "su",
        "sv",
        "sw",
        "ta",
        "te",
        "tg",
        "th",
        "tk",
        "tl",
        "tr",
        "tt",
        "uk",
        "ur",
        "uz",
        "vi",
        "vo",
        "yi",
        "yo",
        "yue",
        "zh",
    ];

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    public ElevenLabsPlugin()
        : this(CreateHttpClient()) { }

    internal ElevenLabsPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.elevenlabs";
    public string PluginName => "ElevenLabs";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync(ApiKeySecretName);
        SelectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "elevenlabs";
    public string ProviderDisplayName => "ElevenLabs";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_modelEntries
            .Select(m => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = true })
            .ToList();

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => s_languages;

    public void SelectModel(string modelId)
    {
        var entry = ResolveModelEntry(modelId);
        SelectedModelId = entry.Id;
        _host?.SetSetting(SelectedModelSettingName, entry.Id);
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

        var entry = ResolveModelEntry(SelectedModelId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/speech-to-text");
        request.Headers.TryAddWithoutValidation("xi-api-key", ApiKey);

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavAudio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");
        form.Add(new StringContent(entry.RestModelId), "model_id");

        if (NormalizeLanguage(language) is { } normalizedLanguage)
            form.Add(new StringContent(normalizedLanguage), "language_code");

        foreach (var term in ExtractKeyterms(prompt))
            form.Add(new StringContent(term), "keyterms");

        request.Content = form;

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ElevenLabs API error {(int)response.StatusCode}: {json}"
            );

        return ParseRestResponse(json, NormalizeLanguage(language));
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured || SelectedModelId is null)
            throw new InvalidOperationException(
                "Plugin not configured. API key and model required."
            );

        var entry = ResolveModelEntry(SelectedModelId);
        return await ElevenLabsStreamingSession.ConnectAsync(
            ApiKey!,
            entry.RealtimeModelId,
            NormalizeLanguage(language),
            ct
        );
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
        var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(ApiKey, normalized, StringComparison.Ordinal);

        ApiKey = normalized;
        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);

            if (changed && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/user");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static PluginTranscriptionResult ParseRestResponse(
        string json,
        string? fallbackLanguage
    )
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl)
            ? textEl.GetString()?.Trim() ?? ""
            : "";
        var detectedLanguage = root.TryGetProperty("language_code", out var langEl)
            ? langEl.GetString()
            : fallbackLanguage;

        var duration = 0.0;
        var segments = new List<PluginTranscriptionSegment>();
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (
            root.TryGetProperty("words", out var wordsEl)
            && wordsEl.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                if (
                    wordEl.TryGetProperty("type", out var typeEl)
                    && !string.Equals(
                        typeEl.GetString(),
                        "word",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }

                var wordText = wordEl.TryGetProperty("text", out var wordTextEl)
                    ? wordTextEl.GetString() ?? ""
                    : "";

                if (
                    string.IsNullOrWhiteSpace(wordText)
                    || !TryGetDouble(wordEl, "start", out var start)
                    || !TryGetDouble(wordEl, "end", out var end)
                )
                {
                    continue;
                }

                segments.Add(new PluginTranscriptionSegment(wordText, start, end));
                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(
            text,
            detectedLanguage,
            duration,
            NoSpeechProbability: null
        )
        {
            Segments = segments,
        };
    }

    internal static IReadOnlyList<string> ExtractKeyterms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (
            var part in prompt.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var term = part.Trim();
            if (
                term.Length == 0
                || term.Length >= 50
                || term.AsSpan().IndexOfAny(s_invalidKeytermCharacters) >= 0
                || term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5
                || !seen.Add(term)
            )
            {
                continue;
            }

            terms.Add(term);
            if (terms.Count == 1000)
                break;
        }

        return terms;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                "xi-...",
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_modelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language)
            ? null
            : language;

    private static string NormalizeModelId(string? modelId) =>
        s_modelEntries.Any(m => m.Id == modelId) ? modelId! : DefaultModelId;

    private static ElevenLabsModelEntry ResolveModelEntry(string modelId) =>
        s_modelEntries.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (
            element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
        )
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    private sealed record ElevenLabsModelEntry(
        string Id,
        string DisplayName,
        string RestModelId,
        string RealtimeModelId
    );
}
