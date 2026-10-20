// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cohere;

public sealed class CoherePlugin
    : ILlmProviderPlugin,
        ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.cohere.com/compatibility";
    private const string TranscriptionUrl = "https://api.cohere.com/v2/audio/transcriptions";
    private const string TranscriptionModelId = "cohere-transcribe-03-2026";

    // Cohere's documented 25 MB limit, read as decimal megabytes to stay on the safe side.
    private const int MaxUploadBytes = 25_000_000;

    // Cohere Transcribe's documented languages; it cannot detect the language itself.
    private static readonly IReadOnlyList<string> s_transcriptionLanguages =
        ["ar", "de", "el", "en", "es", "fr", "it", "ja", "ko", "nl", "pl", "pt", "vi", "zh"];

    private static readonly IReadOnlyList<PluginModelInfo> s_transcriptionModels =
    [
        new(TranscriptionModelId, "Cohere Transcribe")
        {
            IsRecommended = true,
            LanguageCount = s_transcriptionLanguages.Count,
        },
    ];

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private bool _streamResponses = true;

    public CoherePlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal CoherePlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.cohere";
    public string PluginName => "Cohere";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("apiKey");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderName => "Cohere";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public string ProviderId => "cohere";
    public string ProviderDisplayName => "Cohere";
    public bool IsConfigured => IsAvailable;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_transcriptionModels;
    public string SelectedModelId => TranscriptionModelId;
    public bool SupportsTranslation => false;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Unsupported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => s_transcriptionLanguages;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [new("command-a-03-2025", "Command A") { IsRecommended = true }];

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            model,
            systemPrompt,
            userText,
            ct
        );
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!_streamResponses)
        {
            yield return await ProcessAsync(systemPrompt, userText, model, ct);
            yield break;
        }

        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            model,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    public void SelectModel(string modelId)
    {
        if (modelId != TranscriptionModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
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
            throw new NotSupportedException("Translation is not supported by the Cohere plugin.");

        // Defense in depth: the host invoker already rejects Auto and folds regional tags to base codes.
        var languageCode = ResolveTranscriptionLanguage(language);

        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        if (wavAudio.Length > MaxUploadBytes)
            throw new PluginRequestException(Loc.L("Errors.RecordingTooLarge"), PluginRequestFailureKind.RequestTooLarge);

        // Cohere rejects the upload unless every text field precedes the file part.
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(TranscriptionModelId), "model");
        content.Add(new StringContent(languageCode), "language");
        var file = new ByteArrayContent(wavAudio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await OpenAiApiHelper.ReadBodyWithErrorHandlingAsync(
            () => response.Content.ReadAsStringAsync(ct), ct);

        // Cohere returns only the text, so the duration comes from the WAV header.
        return new PluginTranscriptionResult(
            ParseTranscriptionText(json),
            DetectedLanguage: null,
            CalculateWavDurationSeconds(wavAudio)
        );
    }

    private string ResolveTranscriptionLanguage(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            throw new LanguageSelectionNotSupportedException(ProviderId, TranscriptionModelId, LanguageSelection.Automatic);

        var baseCode = trimmed.Split('-', '_')[0].ToLowerInvariant();
        return s_transcriptionLanguages.Contains(baseCode)
            ? baseCode
            : throw new NotSupportedException($"Cohere Transcribe does not support the language '{trimmed}'.");
    }

    private static string ParseTranscriptionText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
            )
                return text.GetString()!.Trim();
        }
        catch (JsonException ex)
        {
            throw InvalidTranscriptionResponse(ex);
        }

        throw InvalidTranscriptionResponse(null);
    }

    private static PluginRequestException InvalidTranscriptionResponse(Exception? inner) =>
        new(
            "Invalid Cohere transcription response: required field 'text' must be a string.",
            PluginRequestFailureKind.OutputIncomplete,
            innerException: inner
        );

    // Bounded chunk walk, so a truncated or hostile header cannot loop forever.
    internal static double CalculateWavDurationSeconds(byte[] wavAudio)
    {
        if (wavAudio.Length < 12
            || Encoding.ASCII.GetString(wavAudio, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(wavAudio, 8, 4) != "WAVE")
        {
            return 0;
        }

        var byteRate = 0;
        var dataSize = 0;
        for (var offset = 12; offset <= wavAudio.Length - 8;)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            if (chunkSize < 0)
                break;

            var dataStart = offset + 8;
            switch (chunkId)
            {
                case "fmt " when chunkSize >= 12 && dataStart <= wavAudio.Length - 12:
                    byteRate = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(dataStart + 8, 4));
                    break;
                case "data":
                    dataSize = Math.Min(chunkSize, Math.Max(0, wavAudio.Length - dataStart));
                    break;
            }

            var paddedChunkSize = (long)chunkSize + (chunkSize & 1);
            if (paddedChunkSize > wavAudio.Length - (long)dataStart)
                break;
            offset = dataStart + (int)paddedChunkSize;
        }

        return byteRate > 0 && dataSize > 0 ? dataSize / (double)byteRate : 0;
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("apiKey");
            else
                await _host.StoreSecretAsync("apiKey", apiKey);

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "apiKey",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "co-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "apiKey" => _apiKey,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
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
            case "apiKey":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }

    public void Dispose() => _httpClient.Dispose();
}
