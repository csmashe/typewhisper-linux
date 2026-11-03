// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.MicrosoftAi;

internal enum MicrosoftAiTranscriptStyle
{
    Clean,
    Verbatim,
}

public sealed class MicrosoftAiPlugin : ITranscriptionEnginePlugin,
    IPluginSettingsProvider, IPluginLocalizationAware, IModelCatalogProvider,
    ITranscriptionLanguageSelectionCapabilities
{
    internal const string DefaultModelId = "MAI-Transcribe-2";
    internal const string LegacyModelId = "MAI-Transcribe-1.5";
    internal const string ApiVersion = "2025-10-15";
    internal const int MaximumAudioBytes = 250_000_000;
    internal const double MaximumAudioDurationSeconds = 2 * 60 * 60;

    private const string ApiKeySecretName = "api-key";
    private const string EndpointSettingName = "endpoint";
    private const string SelectedModelSettingName = "selectedModel";
    private const string CachedModelsSettingName = "cachedModels";
    private const string TranscriptStyleSettingName = "transcriptStyle";
    private const string SpeakerDiarizationSettingName = "speakerDiarizationEnabled";

    private static readonly IReadOnlyList<string> s_fallbackModelIds = [DefaultModelId, LegacyModelId];
    private static readonly IReadOnlyList<string> s_allowedHostSuffixes =
    [
        ".cognitiveservices.azure.com",
        ".api.cognitive.microsoft.com",
        ".services.ai.azure.com",
        ".openai.azure.com",
        ".cognitiveservices.azure.us",
        ".api.cognitive.microsoft.us",
        ".services.ai.azure.us",
        ".openai.azure.us",
        ".cognitiveservices.azure.cn",
        ".api.cognitive.azure.cn",
        ".services.ai.azure.cn",
        ".openai.azure.cn",
    ];
    private static readonly IReadOnlyList<string> s_regionalHostSuffixes =
    [
        ".api.cognitive.microsoft.com",
        ".api.cognitive.microsoft.us",
        ".api.cognitive.azure.cn",
    ];
    private static readonly IReadOnlyList<string> s_supportedMaiRegions =
        ["centralindia", "eastus", "northeurope", "southeastasia", "westus", "westus2"];
    private static readonly IReadOnlyList<string> s_legacyLanguages =
    [
        "ar", "as", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "gu",
        "hi", "hu", "id", "it", "ja", "kn", "ko", "lt", "ml", "mr", "nb", "nl", "or", "pa", "pl",
        "pt", "ro", "ru", "sk", "sl", "sv", "ta", "te", "th", "tr", "uk", "vi", "zh",
    ];
    private static readonly IReadOnlyList<string> s_languages =
    [
        "af", "ar", "as", "az", "bg", "bn", "bs", "ca", "cs", "da", "de", "el", "en", "es", "et",
        "fa", "fi", "fil", "fr", "gl", "gu", "he", "hi", "hu", "hy", "id", "is", "it", "ja", "kk",
        "kn", "ko", "lt", "lv", "mk", "ml", "mr", "ms", "nb", "ne", "nl", "or", "pa", "pl", "pt",
        "ro", "ru", "sk", "sl", "sv", "sw", "ta", "te", "th", "tr", "uk", "ur", "vi", "yue", "zh",
    ];
    private readonly HttpClient _httpClient;
    private readonly Lock _connectionLock = new();
    private long _connectionRevision;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string _endpointValue = "";
    private string _selectedModelId = DefaultModelId;
    private IReadOnlyList<string> _fetchedModelIds = [];
    private bool _speakerDiarizationEnabled;

    public MicrosoftAiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(180) })
    {
    }

    internal MicrosoftAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.microsoft-ai";

    public string PluginName => "Microsoft AI";

    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeSecret(await host.LoadSecretAsync(ApiKeySecretName));
        _endpointValue = NormalizePersistedEndpoint(host.GetSetting<string>(EndpointSettingName));
        _fetchedModelIds = NormalizeModelIds(host.GetSetting<IReadOnlyList<string>>(CachedModelsSettingName) ?? []);

        var storedModel = host.GetSetting<string>(SelectedModelSettingName)?.Trim();
        _selectedModelId = AllModelIds.Contains(storedModel ?? "", StringComparer.OrdinalIgnoreCase)
            ? AllModelIds.First(model => string.Equals(model, storedModel, StringComparison.OrdinalIgnoreCase))
            : DefaultModelId;
        if (!string.Equals(storedModel, _selectedModelId, StringComparison.Ordinal))
            host.SetSetting(SelectedModelSettingName, _selectedModelId);

        TranscriptStyle = string.Equals(
            host.GetSetting<string>(TranscriptStyleSettingName),
            "verbatim",
            StringComparison.OrdinalIgnoreCase)
            ? MicrosoftAiTranscriptStyle.Verbatim
            : MicrosoftAiTranscriptStyle.Clean;
        _speakerDiarizationEnabled =
            host.GetSetting<bool?>(SpeakerDiarizationSettingName) ?? false;
        if (!SelectedModelSupportsDiarization)
            SetSpeakerDiarizationEnabled(false);

        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "microsoft-ai";

    public string ProviderDisplayName => "Microsoft AI (MAI Transcribe)";

    public bool IsConfigured => _apiKey is not null && Endpoint is not null;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
        AllModelIds.Select(CreateModelInfo).ToList();

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract
    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => false;

    public bool SupportsLanguageHints => false;

    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public IReadOnlyList<string> SupportedLanguages => GetSupportedLanguages(_selectedModelId);

    public void SelectModel(string modelId)
    {
        var normalized = modelId.Trim();
        var selected = AllModelIds.FirstOrDefault(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new ArgumentException($"Unknown Microsoft AI transcription model: {modelId}", nameof(modelId));

        if (string.Equals(_selectedModelId, selected, StringComparison.Ordinal))
            return;

        _selectedModelId = selected;
        if (!SelectedModelSupportsDiarization)
            SetSpeakerDiarizationEnabled(false);
        _host?.SetSetting(SelectedModelSettingName, selected);
        _host?.NotifyCapabilitiesChanged();
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeCoreAsync(wavAudio, NormalizeLanguage(language), translate, prompt, ct);

    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new(ApiKeySecretName, Loc.L("Settings.ApiKey"), IsSecret: true,
            Description: Loc.L("Settings.ApiKeyHint"), Kind: PluginSettingKind.Secret),
        new(EndpointSettingName, Loc.L("Settings.Endpoint"),
            Placeholder: Loc.L("Settings.EndpointPlaceholder"),
            Description: Loc.L("Settings.EndpointHint"), Kind: PluginSettingKind.Text),
        new(SelectedModelSettingName, Loc.L("Settings.Model"),
            Description: Loc.L("Settings.ModelHint"),
            Options: TranscriptionModels.Select(model => new PluginSettingOption(model.Id, model.DisplayName)).ToList(),
            Kind: PluginSettingKind.Dropdown),
        new(TranscriptStyleSettingName, Loc.L("Settings.TranscriptStyle"),
            Description: Loc.L("Settings.StyleHint"),
            Options: [new PluginSettingOption("clean", Loc.L("Settings.StyleClean")), new PluginSettingOption("verbatim", Loc.L("Settings.StyleVerbatim"))],
            Kind: PluginSettingKind.Dropdown),
        new(SpeakerDiarizationSettingName, Loc.L("Settings.SpeakerDiarization"),
            Description: Loc.L("Settings.DiarizationHint"), Kind: PluginSettingKind.Boolean),
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            ApiKeySecretName => _apiKey,
            EndpointSettingName => _endpointValue,
            SelectedModelSettingName => _selectedModelId,
            TranscriptStyleSettingName => TranscriptStyle == MicrosoftAiTranscriptStyle.Verbatim ? "verbatim" : "clean",
            SpeakerDiarizationSettingName => _speakerDiarizationEnabled ? "true" : "false",
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? "");
                break;
            case EndpointSettingName:
                SetEndpoint(value ?? "");
                break;
            case SelectedModelSettingName:
                if (AllModelIds.Contains(value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                    SelectModel(value!);
                break;
            case TranscriptStyleSettingName:
                SetTranscriptStyle(string.Equals(value, "verbatim", StringComparison.OrdinalIgnoreCase)
                    ? MicrosoftAiTranscriptStyle.Verbatim : MicrosoftAiTranscriptStyle.Clean);
                break;
            case SpeakerDiarizationSettingName:
                SetSpeakerDiarizationEnabled(bool.TryParse(value, out var enabled) && enabled);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_endpointValue) && _apiKey is null)
            return new PluginSettingsValidationResult(true, Loc.L("Settings.Removed"));
        var endpoint = Endpoint;
        if (endpoint is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.InvalidEndpoint"));
        if (_apiKey is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyRequired"));
        if (GetUnsupportedMaiRegion(endpoint) is { } region)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.RegionUnsupported", region, string.Join(", ", s_supportedMaiRegions)));
        var loaded = await RefreshModelCatalogAsync(ct);
        return new PluginSettingsValidationResult(true, Loc.L(loaded ? "Settings.ModelsRefreshed" : "Settings.ModelsUnavailable"));
    }
    internal MicrosoftAiTranscriptStyle TranscriptStyle { get; private set; } = MicrosoftAiTranscriptStyle.Clean;
    internal bool SelectedModelSupportsDiarization => !IsLegacyModel(_selectedModelId);

    internal void SetEndpoint(string value)
    {
        var normalized = NormalizeEndpoint(value)?.AbsoluteUri.TrimEnd('/') ?? value.Trim();
        lock (_connectionLock)
        {
            if (string.Equals(_endpointValue, normalized, StringComparison.Ordinal))
                return;

            var wasConfigured = IsConfigured;
            _endpointValue = normalized;
            _connectionRevision++;
            _host?.SetSetting(EndpointSettingName, normalized);
            if (_host is not null && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task SetApiKeyAsync(string value)
    {
        var normalized = NormalizeSecret(value);
        lock (_connectionLock)
        {
            if (string.Equals(_apiKey, normalized, StringComparison.Ordinal))
                return;
        }

        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(ApiKeySecretName);
            else
                await _host.StoreSecretAsync(ApiKeySecretName, normalized);
        }

        lock (_connectionLock)
        {
            if (string.Equals(_apiKey, normalized, StringComparison.Ordinal))
                return;

            var wasConfigured = IsConfigured;
            _apiKey = normalized;
            _connectionRevision++;
            if (_host is not null && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SetTranscriptStyle(MicrosoftAiTranscriptStyle value)
    {
        if (TranscriptStyle == value)
            return;
        TranscriptStyle = value;
        _host?.SetSetting(
            TranscriptStyleSettingName,
            value == MicrosoftAiTranscriptStyle.Verbatim ? "verbatim" : "clean");
    }

    internal void SetSpeakerDiarizationEnabled(bool enabled)
    {
        var normalized = enabled && SelectedModelSupportsDiarization;
        if (_speakerDiarizationEnabled == normalized)
            return;
        _speakerDiarizationEnabled = normalized;
        _host?.SetSetting(SpeakerDiarizationSettingName, normalized);
    }

    async Task IModelCatalogProvider.RefreshModelCatalogAsync(CancellationToken ct) =>
        await RefreshModelCatalogAsync(ct);

    internal async Task<bool> RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        Uri? endpoint;
        string? apiKey;
        long connectionRevision;
        lock (_connectionLock)
        {
            endpoint = Endpoint;
            apiKey = _apiKey;
            connectionRevision = _connectionRevision;
        }
        if (endpoint is null || apiKey is null)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelCatalogUri(endpoint));
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Microsoft AI model catalog returned HTTP {(int)response.StatusCode}.");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var fetched = data.EnumerateArray()
                .Select(static item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .OfType<string>();
            var normalized = NormalizeModelIds(fetched);
            if (normalized.Count == 0)
                return false;

            lock (_connectionLock)
            {
                if (connectionRevision != _connectionRevision)
                    return false;

                _fetchedModelIds = normalized;
                _host?.SetSetting(CachedModelsSettingName, normalized);
                if (!AllModelIds.Contains(_selectedModelId, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedModelId = DefaultModelId;
                    _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
                }
                _host?.NotifyCapabilitiesChanged();
                return true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Microsoft AI model catalog refresh failed with {ex.GetType().Name}.");
            return false;
        }
    }

    internal static Uri? NormalizeEndpoint(string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!IsValidResourceName(value))
                return null;
            value = $"https://{value}.cognitiveservices.azure.com";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return s_allowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal))
            ? new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute)
            : null;
    }

    internal static Uri BuildTranscriptionUri(Uri endpoint) =>
        new(endpoint, $"speechtotext/transcriptions:transcribe?api-version={ApiVersion}");

    internal static Uri BuildModelCatalogUri(Uri endpoint) => new(endpoint, "openai/v1/models");

    internal static string? GetRegionalEndpointRegion(Uri endpoint)
    {
        var host = endpoint.IdnHost.ToLowerInvariant();
        var suffix = s_regionalHostSuffixes.FirstOrDefault(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
        if (suffix is null)
            return null;
        var region = host[..^suffix.Length];
        return region.Length > 0 && !region.Contains('.') ? region : null;
    }

    internal static string? GetUnsupportedMaiRegion(Uri endpoint)
    {
        var region = GetRegionalEndpointRegion(endpoint);
        return region is not null
            && !s_supportedMaiRegions.Contains(region, StringComparer.OrdinalIgnoreCase)
            ? region
            : null;
    }

    private static List<string> ExtractDictionaryTerms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalChars = 0;
        foreach (var term in prompt.Split([',', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(term))
                continue;
            if (terms.Count == 500 || totalChars + term.Length > 20_000)
                break;
            terms.Add(term);
            totalChars += term.Length;
        }
        return terms;
    }

    internal static string BuildDefinitionJson(
        string modelId,
        string? language,
        IReadOnlyList<string> phrases,
        MicrosoftAiTranscriptStyle transcriptStyle,
        bool speakerDiarizationEnabled)
    {
        var legacy = IsLegacyModel(modelId);
        var enhancedMode = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["model"] = modelId,
        };
        if (legacy)
        {
            if (transcriptStyle == MicrosoftAiTranscriptStyle.Verbatim)
                enhancedMode["transcribeStyle"] = "verbatim";
        }
        else
        {
            enhancedMode["modelOptions"] = new Dictionary<string, object?>
            {
                ["timestamps"] = "segment",
                ["transcribeStyle"] = transcriptStyle == MicrosoftAiTranscriptStyle.Verbatim
                    ? "verbatim"
                    : "clean",
            };
        }

        var definition = new Dictionary<string, object?>
        {
            ["enhancedMode"] = enhancedMode,
            ["profanityFilterMode"] = "None",
        };
        if (language is not null)
            definition["locales"] = (string[])[language];
        if (speakerDiarizationEnabled && !legacy)
            definition["diarization"] = new Dictionary<string, object?> { ["enabled"] = true };
        if (phrases.Count > 0)
            definition["phraseList"] = new Dictionary<string, object?> { ["phrases"] = phrases };

        return JsonSerializer.Serialize(definition);
    }

    internal static PluginTranscriptionResult ParseResponse(string json, IPluginLocalization? localization = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var hasPhrases = root.TryGetProperty("phrases", out var phrases)
                && phrases.ValueKind == JsonValueKind.Array;
            var hasCombinedPhrases = root.TryGetProperty("combinedPhrases", out var combinedPhrases)
                && combinedPhrases.ValueKind == JsonValueKind.Array;
            if (!hasPhrases && !hasCombinedPhrases)
                throw new PluginRequestException(localization.L("Error.InvalidResponse"),
                    PluginRequestFailureKind.OutputIncomplete);

            var segments = new List<PluginTranscriptionSegment>();
            string? detectedLanguage = null;
            var hasSpeakers = false;

            if (hasPhrases)
            {
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var phraseText = ReadPhraseText(phrase, localization);
                    if (string.IsNullOrEmpty(phraseText))
                        continue;

                    var start = Math.Max(GetOptionalDouble(phrase, "offsetMilliseconds") ?? 0, 0) / 1000d;
                    var duration = Math.Max(GetOptionalDouble(phrase, "durationMilliseconds") ?? 0, 0) / 1000d;
                    var speaker = phrase.TryGetProperty("speaker", out var speakerElement)
                        ? NormalizeSpeakerLabel(speakerElement, localization)
                        : null;
                    hasSpeakers |= speaker is not null;
                    var segmentText = speaker is null ? phraseText : $"{speaker}: {phraseText}";
                    segments.Add(new PluginTranscriptionSegment(segmentText, start, start + duration));

                    if (detectedLanguage is null
                        && phrase.TryGetProperty("locale", out var localeElement)
                        && localeElement.ValueKind == JsonValueKind.String)
                    {
                        detectedLanguage = NormalizeLanguage(localeElement.GetString());
                    }
                }
            }

            var combinedText = ReadCombinedText(root, localization);
            var outputText = hasSpeakers
                ? string.Join("\n", segments.Select(static segment => segment.Text))
                : !string.IsNullOrEmpty(combinedText)
                    ? combinedText
                    : string.Join(" ", segments.Select(static segment => segment.Text));

            var durationSeconds = Math.Max(GetOptionalDouble(root, "durationMilliseconds") ?? 0, 0) / 1000d;
            if (segments.Count > 0)
                durationSeconds = Math.Max(durationSeconds, segments.Max(static segment => segment.End));

            return new PluginTranscriptionResult(
                outputText,
                detectedLanguage,
                durationSeconds,
                NoSpeechProbability: null)
            {
                Segments = segments,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new PluginRequestException(
                localization.L("Error.InvalidResponse"),
                PluginRequestFailureKind.OutputIncomplete,
                innerException: ex);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private Uri? Endpoint => NormalizeEndpoint(_endpointValue);

    private IReadOnlyList<string> AllModelIds => NormalizeModelIds(s_fallbackModelIds.Concat(_fetchedModelIds));

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        var endpoint = Endpoint;
        var apiKey = _apiKey;
        if (endpoint is null || apiKey is null)
        {
            throw new PluginRequestException(
                Loc.L("Error.ConfigurationMissing"),
                PluginRequestFailureKind.Configuration);
        }
        if (translate)
        {
            throw new PluginRequestException(
                Loc.L("Error.TranslationUnsupported"),
                PluginRequestFailureKind.InvalidRequest);
        }
        if (wavAudio.Length >= MaximumAudioBytes
            || TryGetWavDurationSeconds(wavAudio) is >= MaximumAudioDurationSeconds)
        {
            throw new PluginRequestException(
                Loc.L("Error.AudioTooLarge"),
                PluginRequestFailureKind.RequestTooLarge);
        }

        if (IsLegacyModel(_selectedModelId) && language is not null
            && !s_legacyLanguages.Contains(language.Split('-')[0], StringComparer.OrdinalIgnoreCase))
        {
            _host?.Log(PluginLogLevel.Trace,
                $"Language '{language}' is not supported by {_selectedModelId}; using automatic detection.");
            language = null;
        }

        var definitionJson = BuildDefinitionJson(
            _selectedModelId,
            language,
            ExtractDictionaryTerms(prompt),
            TranscriptStyle,
            _speakerDiarizationEnabled);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildTranscriptionUri(endpoint));
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(definitionJson, Encoding.UTF8, "application/json"), "definition");
        var audio = new ByteArrayContent(wavAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "audio.wav");
        request.Content = form;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                Loc.L("Error.NetworkUnreachable"),
                PluginRequestFailureKind.Network,
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                Loc.L("Error.Timeout"),
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }

        using (response)
        {
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ParseResponse(responseJson, Loc)
                : throw CreateHttpFailure(response, responseJson, endpoint, apiKey);
        }
    }

    private PluginRequestException CreateHttpFailure(
        HttpResponseMessage response,
        string responseBody,
        Uri endpoint,
        string apiKey)
    {
        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
            retryAfter = retryAt - DateTimeOffset.UtcNow;

        var summary = ExtractErrorSummary(responseBody, apiKey);
        if (statusCode == 400
            && summary.Contains("enhanced mode with model", StringComparison.OrdinalIgnoreCase)
            && GetUnsupportedMaiRegion(endpoint) is { } region)
        {
            var regionMessage = Loc.L("Settings.RegionUnsupported",
                region, string.Join(", ", s_supportedMaiRegions));
            return new PluginRequestException(
                regionMessage,
                PluginRequestFailureKind.InvalidRequest,
                statusCode);
        }

        var kind = statusCode switch
        {
            401 or 403 => PluginRequestFailureKind.Authentication,
            408 => PluginRequestFailureKind.Timeout,
            413 => PluginRequestFailureKind.RequestTooLarge,
            429 => PluginRequestFailureKind.RateLimit,
            >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
            >= 400 and <= 499 => PluginRequestFailureKind.InvalidRequest,
            _ => PluginRequestFailureKind.Unknown,
        };
        var message = statusCode switch
        {
            401 or 403 => Loc.L("Error.Authentication"),
            413 => Loc.L("Error.SizeLimit"),
            429 => Loc.L("Error.RateLimit"),
            _ => Loc.L("Error.HttpFailure", statusCode, summary),
        };
        return new PluginRequestException(message, kind, statusCode, retryAfter);
    }

    private string ExtractErrorSummary(string body, string apiKey)
    {
        var summary = Loc.L("Error.ServerError");
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (TryReadErrorText(root, out var errorText))
                    summary = errorText;
            }
            catch (JsonException)
            {
                summary = body.Trim();
            }
        }

        summary = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrEmpty(apiKey))
            summary = summary.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return summary.Length <= 500 ? summary : summary[..500] + "…";
    }

    private static bool TryReadErrorText(JsonElement root, out string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            text = "";
            return false;
        }

        foreach (var propertyName in (string[])["message", "detail", "code"])
        {
            if (!root.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.GetString()))
                continue;
            text = property.GetString()!;
            return true;
        }

        text = "";
        if (!root.TryGetProperty("error", out var error))
            return false;
        // ReSharper disable once InvertIf -- handle a direct error string before the recursive object fallback.
        if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            text = error.GetString()!;
            return true;
        }
        return error.ValueKind == JsonValueKind.Object && TryReadErrorText(error, out text);
    }

    private static string ReadCombinedText(JsonElement root, IPluginLocalization? localization)
    {
        if (!root.TryGetProperty("combinedPhrases", out var combined)
            || combined.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Join(
            "\n",
            combined.EnumerateArray()
                .Select(phrase => ReadPhraseText(phrase, localization))
                .Where(static text => !string.IsNullOrEmpty(text)));
    }

    private static string ReadPhraseText(JsonElement phrase, IPluginLocalization? localization)
    {
        if (phrase.ValueKind != JsonValueKind.Object
            || !phrase.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            throw new PluginRequestException(localization.L("Error.InvalidResponse"),
                PluginRequestFailureKind.OutputIncomplete);
        }

        return text.GetString()!.Trim();
    }

    private static string? NormalizeSpeakerLabel(JsonElement speaker, IPluginLocalization? localization)
    {
        var value = speaker.ValueKind switch
        {
            JsonValueKind.String => speaker.GetString(),
            JsonValueKind.Number => speaker.GetRawText(),
            _ => null,
        };
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return trimmed.Contains("speaker", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : localization?.GetString("Transcript.SpeakerLabel", trimmed) ?? $"Speaker {trimmed}";
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
            ? value
            : null;

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().Replace('_', '-');
        return string.IsNullOrEmpty(normalized)
            || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string NormalizePersistedEndpoint(string? endpoint) =>
        NormalizeEndpoint(endpoint)?.AbsoluteUri.TrimEnd('/') ?? endpoint?.Trim() ?? "";

    private static string? NormalizeSecret(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsValidResourceName(string value)
    {
        if (value.Length is < 1 or > 64
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsLegacyModel(string modelId) =>
        string.Equals(modelId, LegacyModelId, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetSupportedLanguages(string modelId) =>
        IsLegacyModel(modelId) ? s_legacyLanguages : s_languages;

    private static List<string> NormalizeModelIds(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values
            .Select(static value => value.Trim())
            .Where(static value => value.Contains("mai-transcribe", StringComparison.OrdinalIgnoreCase))
            .Where(seen.Add)
            .OrderByDescending(static value =>
                string.Equals(value, DefaultModelId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static value => IsLegacyModel(value))
            .ThenByDescending(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PluginModelInfo CreateModelInfo(string modelId)
    {
        var displayName = string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase)
            ? "MAI Transcribe 2"
            : IsLegacyModel(modelId)
                ? "MAI Transcribe 1.5"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(modelId.Replace('-', ' ').ToLowerInvariant());
        return new PluginModelInfo(modelId, displayName)
        {
            IsRecommended = string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase),
            LanguageCount = GetSupportedLanguages(modelId).Count,
        };
    }

    private static double? TryGetWavDurationSeconds(byte[] wavAudio)
    {
        if (wavAudio.Length < 12
            || !wavAudio.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !wavAudio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        uint byteRate = 0;
        uint dataSize = 0;
        var offset = 12;
        while (offset <= wavAudio.Length - 8)
        {
            var chunkId = wavAudio.AsSpan(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            var contentOffset = offset + 8;
            if (chunkSize > int.MaxValue || contentOffset > wavAudio.Length - (int)chunkSize)
                break;

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 12)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wavAudio.AsSpan(contentOffset + 8, 4));
            else if (chunkId.SequenceEqual("data"u8))
                dataSize = chunkSize;

            var paddedSize = (long)chunkSize + (chunkSize & 1);
            if (paddedSize > int.MaxValue - contentOffset)
                break;
            offset = contentOffset + (int)paddedSize;
        }

        return byteRate > 0 && dataSize > 0 ? dataSize / (double)byteRate : null;
    }
}
