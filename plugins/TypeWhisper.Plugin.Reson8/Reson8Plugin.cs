// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public sealed class Reson8Plugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    internal const string DefaultModelId = "__default__";
    internal const string DefaultBaseUrl = "https://api.reson8.dev";
    internal const string DefaultAuthHeader = "Authorization";

    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CustomBaseUrlSettingName = "customBaseURL";
    private const string CustomAuthHeaderSettingName = "customAuthHeader";
    private const string FetchedCustomModelsSettingName = "fetchedCustomModels";

    private static readonly IReadOnlyList<string> s_languages =
    [
        "nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string _selectedModelId = DefaultModelId;

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
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        CustomBaseUrl = NormalizeBaseUrl(host.GetSetting<string>(CustomBaseUrlSettingName));
        CustomAuthHeader = NormalizeAuthHeader(host.GetSetting<string>(CustomAuthHeaderSettingName));
        FetchedCustomModels = host.GetSetting<List<Reson8CustomModel>>(FetchedCustomModelsSettingName) ?? [];
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
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        [new(DefaultModelId, Loc.L("Settings.DefaultModel")), .. FetchedCustomModels.Select(m => new PluginModelInfo(m.Id, m.Name))];
    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => s_languages;

    internal string? ApiKey { get; private set; }

    internal string CustomBaseUrl { get; private set; } = DefaultBaseUrl;

    internal string CustomAuthHeader { get; private set; } = DefaultAuthHeader;

    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels { get; private set; } = [];

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
            BuildPrerecordedUri(CustomBaseUrl, _selectedModelId, NormalizeLanguage(language)));
        AddAuthHeader(request, ApiKey!, CustomAuthHeader);
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

        return await RunStreamingWithBatchFallbackAsync(
            async markProgressStopped =>
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
                    if (string.IsNullOrWhiteSpace(text) || onProgress(text))
                        return;

                    markProgressStopped();
                    // ReSharper disable once AccessToDisposedClosure -- the closure runs only within the using-scope (or the source is disposed after the captured resource), so the access is safe.
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
                    ? null
                    : new PluginTranscriptionResult(
                        text,
                        NormalizeLanguage(language),
                        PcmDurationSeconds(pcm16.Length),
                        NoSpeechProbability: null
                    );
            },
            () => TranscribeAsync(wavAudio, language, translate, prompt, ct),
            ct
        );
    }

    internal static async Task<T> RunStreamingWithBatchFallbackAsync<T>(
        Func<Action, Task<T?>> runStreaming,
        Func<Task<T>> runBatch,
        CancellationToken callerToken
    ) where T : class
    {
        var progressStopped = 0;
        T? streamed;
        try
        {
            streamed = await runStreaming(() => Volatile.Write(ref progressStopped, 1));
        }
        catch (Exception) when (callerToken.IsCancellationRequested)
        {
            // Caller cancellation has precedence over a simultaneous dependency fault.
            throw new OperationCanceledException(callerToken);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref progressStopped) != 0)
        {
            throw;
        }
        catch (Exception) when (Volatile.Read(ref progressStopped) != 0)
        {
            throw new OperationCanceledException("Streaming stopped by the progress callback.");
        }
        catch
        {
            return await runBatch();
        }

        callerToken.ThrowIfCancellationRequested();
        return streamed ?? await runBatch();
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        return await Reson8StreamingSession.ConnectAsync(
            ApiKey!,
            CustomBaseUrl,
            CustomAuthHeader,
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
                Description: FetchedCustomModels.Count > 0
                    ? Loc.L("Settings.CustomModelsLoaded", FetchedCustomModels.Count)
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
                ApiKeySecretName => ApiKey,
                SelectedModelSettingName => _selectedModelId,
                CustomBaseUrlSettingName => CustomBaseUrl == DefaultBaseUrl ? null : CustomBaseUrl,
                CustomAuthHeaderSettingName => CustomAuthHeader == DefaultAuthHeader ? null : CustomAuthHeader,
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
        if (string.IsNullOrEmpty(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
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
            var changed = !string.Equals(ApiKey, normalized, StringComparison.Ordinal);

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
            ApiKey = normalized;

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
            BuildPrerecordedUri(CustomBaseUrl, DefaultModelId, language: null));
        AddAuthHeader(request, normalized, CustomAuthHeader);
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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CustomBaseUrl}/v1/custom-model");
        AddAuthHeader(request, ApiKey!, CustomAuthHeader);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<Reson8CustomModel>>(json, s_jsonOptions) ?? [];
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
        FetchedCustomModels = models.ToArray();
        _host?.SetSetting(FetchedCustomModelsSettingName, FetchedCustomModels);

        if (_selectedModelId != DefaultModelId && FetchedCustomModels.All(m => m.Id != _selectedModelId))
        {
            _selectedModelId = DefaultModelId;
            _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
        }

        _host?.NotifyCapabilitiesChanged();
    }

    internal void SetCustomBaseUrl(string? url)
    {
        CustomBaseUrl = NormalizeBaseUrl(url);
        _host?.SetSetting(CustomBaseUrlSettingName, CustomBaseUrl == DefaultBaseUrl ? null : CustomBaseUrl);
    }

    internal void SetCustomAuthHeader(string? header)
    {
        CustomAuthHeader = NormalizeAuthHeader(header);
        _host?.SetSetting(CustomAuthHeaderSettingName, CustomAuthHeader == DefaultAuthHeader ? null : CustomAuthHeader);
    }

    internal static Uri BuildPrerecordedUri(string baseUrl, string? modelId, string? language)
    {
        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1",
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

    // The typed invoker maps "auto" to null already; this only catches direct/legacy callers.
    internal static string? NormalizeLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

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
        while (normalized.EndsWith('/'))
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
        // ReSharper disable once ConvertSwitchStatementToSwitchExpression -- subjective style; the statement switch reads fine here.
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- the default arm intentionally covers the remaining enum values.
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
        var sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (offset + 8 <= wavAudio.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BitConverter.ToUInt32(wavAudio, offset + 4);
            offset += 8;
            var remaining = wavAudio.Length - offset;

            if (chunkId == "data")
            {
                // A non-seekable muxer (ffmpeg's `-f wav pipe:1`) can't backfill
                // the data size and writes 0xFFFFFFFF; treat any size past the
                // buffer end as "everything remaining".
                var dataLength = chunkSize > (uint)remaining ? remaining : (int)chunkSize;
                data = wavAudio.Skip(offset).Take(dataLength).ToArray();
                offset += dataLength + dataLength % 2;
                continue;
            }

            // Any other chunk claiming more than the buffer holds means a
            // truncated or corrupt file, so stop scanning.
            if (chunkSize > (uint)remaining)
                break;

            var size = (int)chunkSize;
            if (chunkId == "fmt " && size >= 16)
            {
                audioFormat = BitConverter.ToInt16(wavAudio, offset);
                channels = BitConverter.ToInt16(wavAudio, offset + 2);
                sampleRate = BitConverter.ToInt32(wavAudio, offset + 4);
                bitsPerSample = BitConverter.ToInt16(wavAudio, offset + 14);
            }

            offset += size + size % 2;
        }

        if (data is null)
            return wavAudio;

        // The endpoints advertise the body as raw pcm_s16le/16 kHz/mono, so any
        // other format would be mislabeled and transcribed as noise; reject it.
        if (audioFormat != 1 || channels != 1 || sampleRate != 16000 || bitsPerSample != 16)
        {
            throw new NotSupportedException(
                "Reson8 requires 16-bit little-endian PCM, 16 kHz, mono audio, but received "
                + $"format={audioFormat}, channels={channels}, sampleRate={sampleRate}, bitsPerSample={bitsPerSample}.");
        }

        return data;
    }

    private static bool HasAscii(byte[] bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length)
            return false;

        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; clearer than the LINQ form here.
        for (var i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != value[i])
                return false;
        }

        return true;
    }
}
