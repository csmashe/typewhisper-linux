using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public sealed class Reson8Plugin : ITranscriptionEnginePlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    internal const string DefaultModelId = "__default__";
    internal const string DefaultBaseUrl = "https://api.reson8.dev";
    internal const string DefaultAuthHeader = "Authorization";

    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CustomBaseUrlSettingName = "customBaseURL";
    private const string CustomAuthHeaderSettingName = "customAuthHeader";
    private const string FetchedCustomModelsSettingName = "fetchedCustomModels";

    private static readonly IReadOnlyList<string> Languages =
    [
        "nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _selectedModelId = DefaultModelId;
    private string _customBaseUrl = DefaultBaseUrl;
    private string _customAuthHeader = DefaultAuthHeader;
    private IReadOnlyList<Reson8CustomModel> _fetchedCustomModels = [];

    public Reson8Plugin()
        : this(CreateHttpClient())
    {
    }

    internal Reson8Plugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.reson8";
    public string PluginName => "Reson8";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _customBaseUrl = NormalizeBaseUrl(host.GetSetting<string>(CustomBaseUrlSettingName));
        _customAuthHeader = NormalizeAuthHeader(host.GetSetting<string>(CustomAuthHeaderSettingName));
        _fetchedCustomModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName) ?? [];
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "reson8";
    public string ProviderDisplayName => "Reson8";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        [new PluginModelInfo(DefaultModelId, Loc.L("Settings.DefaultModel")), .. _fetchedCustomModels.Select(m => new PluginModelInfo(m.Id, m.Name))];
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public IReadOnlyList<string> SupportedLanguages => Languages;

    internal string? ApiKey => _apiKey;
    internal string CustomBaseUrl => _customBaseUrl;
    internal string CustomAuthHeader => _customAuthHeader;
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels => _fetchedCustomModels;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public void SelectModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        _selectedModelId = normalized;
        _host?.SetSetting(SelectedModelSettingName, normalized);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(_customBaseUrl, _selectedModelId, NormalizeLanguage(language)));
        AddAuthHeader(request, _apiKey!, _customAuthHeader);
        request.Content = new ByteArrayContent(pcm16);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            ThrowForApiError(response.StatusCode, json);

        return ParseTranscriptionResponse(json, NormalizeLanguage(language), pcm16.Length);
    }

    public async Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        try
        {
            var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);

            // onProgress returning false means the host wants to stop (e.g.
            // recording ended). Honor it by cancelling a token linked to ct so
            // the send/finalize flow halts instead of streaming the whole clip
            // and returning a result the caller no longer wants.
            using var streamingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var session = await StartStreamingAsync(language, streamingCts.Token);
            var collector = new Reson8TranscriptCollector();

            session.TranscriptReceived += evt =>
            {
                var text = collector.ApplyEvent(evt);
                if (!string.IsNullOrWhiteSpace(text) && !onProgress(text))
                    streamingCts.Cancel();
            };

            const int chunkSize = 8192;
            for (var offset = 0;
                offset < pcm16.Length && !streamingCts.IsCancellationRequested;
                offset += chunkSize)
            {
                var count = Math.Min(chunkSize, pcm16.Length - offset);
                await session.SendAudioAsync(pcm16.AsMemory(offset, count), streamingCts.Token);
            }

            streamingCts.Token.ThrowIfCancellationRequested();
            await session.FinalizeAsync(streamingCts.Token);

            var text = collector.FinalText;
            return string.IsNullOrWhiteSpace(text)
                ? await TranscribeAsync(wavAudio, language, translate, prompt, ct)
                : new PluginTranscriptionResult(text, NormalizeLanguage(language), PcmDurationSeconds(pcm16.Length), NoSpeechProbability: null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return await TranscribeAsync(wavAudio, language, translate, prompt, ct);
        }
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        return await Reson8StreamingSession.ConnectAsync(
            _apiKey!,
            _customBaseUrl,
            _customAuthHeader,
            _selectedModelId,
            NormalizeLanguage(language),
            ct);
    }

    // IPluginSettingsProvider

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Description: Loc.L("Settings.ApiKeyDescription")),
            new(
                Key: SelectedModelSettingName,
                Label: Loc.L("Settings.Model"),
                Description: _fetchedCustomModels.Count > 0
                    ? Loc.L("Settings.CustomModelsLoaded", _fetchedCustomModels.Count)
                    : Loc.L("Settings.NoCustomModels"),
                Options: TranscriptionModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown),
            new(
                Key: CustomBaseUrlSettingName,
                Label: Loc.L("Settings.CustomBaseUrl"),
                Placeholder: DefaultBaseUrl,
                Description: Loc.L("Settings.CustomBaseUrlDescription"),
                Kind: PluginSettingKind.Text),
            new(
                Key: CustomAuthHeaderSettingName,
                Label: Loc.L("Settings.CustomAuthHeader"),
                Placeholder: DefaultAuthHeader,
                Description: Loc.L("Settings.CustomAuthHeaderDescription"),
                Kind: PluginSettingKind.Text),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                ApiKeySecretName => _apiKey,
                SelectedModelSettingName => _selectedModelId,
                CustomBaseUrlSettingName => _customBaseUrl == DefaultBaseUrl ? null : _customBaseUrl,
                CustomAuthHeaderSettingName => _customAuthHeader == DefaultAuthHeader ? null : _customAuthHeader,
                _ => null,
            });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                if (!IsConfigured)
                    SetFetchedCustomModels([]);
                break;
            case SelectedModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case CustomBaseUrlSettingName:
                SetCustomBaseUrl(value);
                break;
            case CustomAuthHeaderSettingName:
                SetCustomAuthHeader(value);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.InvalidApiKey"));

        // A valid key gates the custom model catalog; refresh it so the model
        // dropdown reflects the account's custom models (mirrors the desktop
        // "Test" → "Refresh models" flow).
        var models = await FetchCustomModelsAsync(ct);
        SetFetchedCustomModels(models);

        return new PluginSettingsValidationResult(
            true,
            models.Count > 0
                ? Loc.L("Settings.ApiKeyValidWithModels", models.Count)
                : Loc.L("Settings.ApiKeyValidShort"));
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        IPluginHostServices? hostToNotify = null;

        await _apiKeyWriteLock.WaitAsync();
        try
        {
            var wasConfigured = IsConfigured;
            var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

            if (!changed)
                return;

            if (_host is not null)
            {
                if (normalized is null)
                    await _host.DeleteSecretAsync(ApiKeySecretName);
                else
                    await _host.StoreSecretAsync(ApiKeySecretName, normalized);

                hostToNotify = _host;
            }

            // Update in-memory state only after the secret write/delete
            // succeeds, so a failing store leaves the plugin unconfigured (no
            // unsaved key) and a failing delete keeps the running key intact.
            _apiKey = normalized;

            if (wasConfigured == IsConfigured)
                hostToNotify = null;
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        hostToNotify?.NotifyCapabilitiesChanged();
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(_customBaseUrl, DefaultModelId, language: null));
        AddAuthHeader(request, normalized, _customAuthHeader);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.StatusCode != HttpStatusCode.Unauthorized;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal async Task<IReadOnlyList<Reson8CustomModel>> FetchCustomModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_customBaseUrl}/v1/custom-model");
        AddAuthHeader(request, _apiKey!, _customAuthHeader);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<Reson8CustomModel>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models)
    {
        _fetchedCustomModels = models.ToArray();
        _host?.SetSetting(FetchedCustomModelsSettingName, _fetchedCustomModels);

        if (_selectedModelId != DefaultModelId && _fetchedCustomModels.All(m => m.Id != _selectedModelId))
        {
            _selectedModelId = DefaultModelId;
            _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
        }

        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomBaseUrl(string? url)
    {
        _customBaseUrl = NormalizeBaseUrl(url);
        _host?.SetSetting(CustomBaseUrlSettingName, _customBaseUrl == DefaultBaseUrl ? null : _customBaseUrl);
    }

    internal void SetCustomAuthHeader(string? header)
    {
        _customAuthHeader = NormalizeAuthHeader(header);
        _host?.SetSetting(CustomAuthHeaderSettingName, _customAuthHeader == DefaultAuthHeader ? null : _customAuthHeader);
    }

    internal static Uri BuildPrerecordedUri(string baseUrl, string? modelId, string? language)
    {
        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1"
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language={Uri.EscapeDataString(language)}");

        if (!string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId)}");
        }

        return new Uri($"{NormalizeBaseUrl(baseUrl)}/v1/speech-to-text/prerecorded?{string.Join("&", query)}");
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? fallbackLanguage,
        int pcm16ByteLength)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = GetString(root, "text")?.Trim() ?? "";
        var language = GetString(root, "language") ?? GetString(root, "detected_language") ?? fallbackLanguage;
        return new PluginTranscriptionResult(text, language, PcmDurationSeconds(pcm16ByteLength), NoSpeechProbability: null);
    }

    internal static string ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (GetString(root, "code") is { } code && GetString(root, "message") is { } codeMessage)
                return $"{code}: {codeMessage}";

            return GetString(root, "message")
                ?? GetString(root, "error")
                ?? GetString(root, "detail")
                ?? "Unknown error";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
        }
    }

    internal static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim();

    internal static void AddAuthHeader(HttpRequestMessage request, string apiKey, string authHeader)
    {
        var normalizedHeader = NormalizeAuthHeader(authHeader);
        if (string.Equals(normalizedHeader, DefaultAuthHeader, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation(normalizedHeader, apiKey);
    }

    internal static string AuthHeaderValue(string apiKey, string authHeader) =>
        string.Equals(NormalizeAuthHeader(authHeader), DefaultAuthHeader, StringComparison.OrdinalIgnoreCase)
            ? $"ApiKey {apiKey}"
            : apiKey;

    private static string NormalizeModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string NormalizeBaseUrl(string? url)
    {
        var normalized = string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim();
        while (normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];

        return string.IsNullOrWhiteSpace(normalized) ? DefaultBaseUrl : normalized;
    }

    private static string NormalizeAuthHeader(string? header) =>
        string.IsNullOrWhiteSpace(header) ? DefaultAuthHeader : header.Trim();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private void ThrowForApiError(HttpStatusCode statusCode, string json)
    {
        var message = ExtractApiError(json);
        switch (statusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new UnauthorizedAccessException(Loc.L("Settings.InvalidApiKey"));
            case HttpStatusCode.NotFound:
                throw new KeyNotFoundException($"Reson8 custom model not found: {message}");
            case HttpStatusCode.RequestEntityTooLarge:
                throw new InvalidOperationException($"Reson8 file too large: {message}");
            case HttpStatusCode.TooManyRequests:
                throw new HttpRequestException($"Reson8 rate limit exceeded: {message}");
            case HttpStatusCode.InternalServerError:
                throw new HttpRequestException($"Reson8 server error: {message}");
            default:
                throw new HttpRequestException($"Reson8 API error {(int)statusCode}: {message}");
        }
    }

    private static double PcmDurationSeconds(int pcm16ByteLength) =>
        pcm16ByteLength <= 0 ? 0 : pcm16ByteLength / 2.0 / 16000.0;

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}

internal static class WavPcm16Extractor
{
    public static byte[] ExtractPcm16(byte[] wavAudio)
    {
        if (wavAudio.Length < 44
            || !HasAscii(wavAudio, 0, "RIFF")
            || !HasAscii(wavAudio, 8, "WAVE"))
        {
            return wavAudio;
        }

        var offset = 12;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (offset + 8 <= wavAudio.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BitConverter.ToInt32(wavAudio, offset + 4);
            offset += 8;
            if (chunkSize < 0 || offset + chunkSize > wavAudio.Length)
                break;

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToInt16(wavAudio, offset);
                channels = BitConverter.ToInt16(wavAudio, offset + 2);
                sampleRate = BitConverter.ToInt32(wavAudio, offset + 4);
                bitsPerSample = BitConverter.ToInt16(wavAudio, offset + 14);
            }
            else if (chunkId == "data")
            {
                data = wavAudio.Skip(offset).Take(chunkSize).ToArray();
            }

            offset += chunkSize + (chunkSize % 2);
        }

        if (data is null)
            return wavAudio;

        if (audioFormat == 1 && channels == 1 && sampleRate == 16000 && bitsPerSample == 16)
            return data;

        return data;
    }

    private static bool HasAscii(byte[] bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != value[i])
                return false;
        }

        return true;
    }
}
