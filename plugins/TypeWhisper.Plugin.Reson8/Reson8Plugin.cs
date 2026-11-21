// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public sealed partial class Reson8Plugin
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
    private const string FillerModeSettingName = "fillerMode";
    private const string BiasStrengthSettingName = "biasStrength";
    private const string DefaultBiasChoice = "default";

    // Reson8's bias_strength when none is sent.
    internal const double DefaultBiasStrength = 0.45;

    private static readonly IReadOnlyList<string> s_languages =
    [
        "nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv",
    ];

    private static readonly DictionaryTermsBudget s_phrasesBudget = new(MaxTerms: 250, MaxCharsPerTerm: 255);

    // Phrases go in the request URL; this keeps it inside common request-line limits.
    internal const int MaxPhrasesQueryLength = 6000;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string _selectedModelId = DefaultModelId;

    // A redirect would forward a custom auth header to the new host, so none are followed.
    public Reson8Plugin()
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) })
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
        RestoreCatalog(host);
        _selectedModelId = NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        FillerMode = Reson8FillerModes.Normalize(host.GetSetting<string>(FillerModeSettingName));
        BiasStrength = ParseBiasStrength(host.GetSetting<string>(BiasStrengthSettingName));
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

    // The default keeps its id so saved selections stay valid; custom models run on it.
    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var listed = FetchedListedModels;
            var defaultModel = DefaultListedModel(listed);
            var defaultLanguages = LanguagesOf(defaultModel);
            return
            [
                ModelInfo(DefaultModelId, defaultModel?.DisplayName ?? Loc.L("Settings.DefaultModel"), defaultLanguages),
                .. NonDefaultListedModels(listed).Select(m => ModelInfo(m.Id, m.DisplayName, LanguagesOf(m))),
                .. FetchedCustomModels
                    .Where(m => listed.All(l => l.Id != m.Id))
                    .Select(m => ModelInfo(m.Id, m.Name, defaultLanguages)),
            ];
        }
    }

    private static PluginModelInfo ModelInfo(string id, string name, IReadOnlyList<string> languages) =>
        new(id, name) { LanguageCount = languages.Count };

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    // Rumble-2-Flash transcribes recordings only; an unlisted model is assumed to stream.
    public bool SupportsStreaming => CaptureConnection().Model?.Streams ?? true;
    public bool SupportsLanguageHints => true;
    public bool SupportsStructuredDictionaryTerms => true;
    public DictionaryTermsBudget DictionaryTermsBudget => s_phrasesBudget;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => LanguagesOf(CaptureConnection().Model);

    private static IReadOnlyList<string> LanguagesOf(Reson8Model? model) =>
        model?.Languages is { Count: > 0 } languages ? languages : s_languages;

    internal string? ApiKey { get; private set; }

    internal string CustomBaseUrl { get; private set; } = DefaultBaseUrl;

    internal string CustomAuthHeader { get; private set; } = DefaultAuthHeader;

    internal TimeSpan StreamingConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal TimeSpan StreamingFinalizeGrace { get; init; } = TimeSpan.FromSeconds(15);

    internal Func<Reson8StreamingRequest, CancellationToken, Task<IStreamingSession>>? StreamingSessionFactory { get; init; }

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
        bool capabilitiesChanged;
        lock (_catalogLock)
        {
            var before = CapabilitySnapshot();
            _host?.SetSetting(SelectedModelSettingName, normalized);
            _selectedModelId = normalized;
            capabilitiesChanged = CapabilitySnapshot() != before;
        }

        if (capabilitiesChanged)
            _host?.NotifyCapabilitiesChanged();
    }

    private (bool Streaming, string Languages) CapabilitySnapshot() =>
        (SupportsStreaming, string.Join(",", SupportedLanguages));

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeCoreAsync(wavAudio, NormalizeLanguage(language), translate, prompt, ct);

    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct) =>
        TranscribeCoreAsync(wavAudio, AdvisoryLanguageList(languageHints), translate, prompt, ct);

    // language is one code, which pins it, or a comma-separated list Reson8 detects among.
    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        byte[] wavAudio,
        string? language,
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the one translation check for every batch path, including the streaming fallback.
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        var connection = EnsureReadyForRequest();
        var phrases = PhrasesFor(prompt, connection);

        var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(connection.BaseUrl, connection.ModelId, language, phrases, QueryOptionsFor(connection)));
        AddAuthHeader(request, connection.ApiKey, connection.AuthHeader);
        request.Content = new ByteArrayContent(pcm16);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => FormatApiError(errorResponse.StatusCode, errorBody, connection.IsCustomModel));
        var json = await response.Content.ReadAsStringAsync(ct);

        return ParseTranscriptionResponse(json, PinnedLanguage(language), pcm16.Length);
    }

    public Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct) =>
        TranscribeStreamingCoreAsync(wavAudio, NormalizeLanguage(language), translate, prompt, onProgress, ct);

    public Task<PluginTranscriptionResult> TranscribeStreamingWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct) =>
        TranscribeStreamingCoreAsync(wavAudio, AdvisoryLanguageList(languageHints), translate, prompt, onProgress, ct);

    private async Task<PluginTranscriptionResult> TranscribeStreamingCoreAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Reson8 does not support translation.");

        var connection = EnsureReadyForRequest();
        if (connection.Model?.Streams == false)
            return await TranscribeCoreAsync(wavAudio, language, translate, prompt, ct);

        var streamingRequest = new Reson8StreamingRequest(
            language,
            PhrasesFor(prompt, connection),
            connection.ModelId,
            QueryOptionsFor(connection));

        return await RunStreamingWithBatchFallbackAsync(
            async markProgressStopped =>
            {
                var pcm16 = WavPcm16Extractor.ExtractPcm16(wavAudio);

                // onProgress returning false means the host wants to stop (e.g.
                // recording ended). Honor it by cancelling a token linked to ct so
                // the send/finalize flow halts instead of streaming the whole clip
                // and returning a result the caller no longer wants.
                using var streamingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await using var session = await WithDeadlineAsync(
                    token => ConnectStreamingSessionAsync(streamingRequest, token),
                    StreamingConnectTimeout,
                    "The Reson8 streaming connection timed out.",
                    streamingCts.Token);
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
                // Recorded audio is sent faster than realtime, so the server may still be
                // transcribing up to its full duration when the flush request goes out.
                await WithDeadlineAsync(
                    async token =>
                    {
                        await session.FinalizeAsync(token);
                        return true;
                    },
                    StreamingFinalizeGrace + TimeSpan.FromSeconds(PcmDurationSeconds(pcm16.Length)),
                    "Reson8 did not confirm the end of the stream in time.",
                    streamingCts.Token);

                var text = collector.FinalText;
                return string.IsNullOrWhiteSpace(text)
                    ? null
                    : new PluginTranscriptionResult(
                        text,
                        PinnedLanguage(language) ?? collector.DetectedLanguage,
                        PcmDurationSeconds(pcm16.Length),
                        NoSpeechProbability: null
                    );
            },
            () => TranscribeCoreAsync(wavAudio, language, translate, prompt, ct),
            ct
        );
    }

    // A TimeoutException falls back to batch; caller cancellation does not.
    private static async Task<T> WithDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan deadline,
        string timeoutMessage,
        CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await operation(linked.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage, ex);
        }
    }

    private Task<IStreamingSession> ConnectStreamingSessionAsync(Reson8StreamingRequest request, CancellationToken ct) =>
        StreamingSessionFactory?.Invoke(request, ct) ?? StartStreamingCoreAsync(request, ct);

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

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        ConnectStreamingSessionAsync(new Reson8StreamingRequest(NormalizeLanguage(language), []), ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct) =>
        ConnectStreamingSessionAsync(new Reson8StreamingRequest(AdvisoryLanguageList(languageHints), []), ct);

    // This call cannot tell an explicit language from an advisory hint, so a lone hint pins the
    // session. Dictation, its only caller, sends hints only when a language is selected.
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct)
    {
        var connection = EnsureReadyForRequest();
        EnsureStreams(connection);
        return ConnectStreamingSessionAsync(
            new Reson8StreamingRequest(
                LanguageList(languageHints),
                PhrasesFor(prompt, connection),
                connection.ModelId,
                QueryOptionsFor(connection)),
            ct);
    }

    private async Task<IStreamingSession> StartStreamingCoreAsync(Reson8StreamingRequest request, CancellationToken ct)
    {
        var connection = EnsureReadyForRequest();
        // A request with a model was checked against the selection it was built for.
        if (request.ModelId is null)
            EnsureStreams(connection);

        return await Reson8StreamingSession.ConnectAsync(
            connection.ApiKey,
            connection.BaseUrl,
            connection.AuthHeader,
            request.ModelId ?? connection.ModelId,
            request.Language,
            request.Phrases,
            request.Options ?? QueryOptionsFor(connection),
            ct);
    }

    private void EnsureStreams(Reson8Connection connection)
    {
        if (connection.Model?.Streams == false)
            throw new PluginRequestException(Loc.L("Settings.ModelDoesNotStream"), PluginRequestFailureKind.Configuration);
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
                Options: ModelOptions(),
                Kind: PluginSettingKind.Dropdown),
            new(
                Key: FillerModeSettingName,
                Label: Loc.L("Settings.FillerMode"),
                Description: Loc.L("Settings.FillerModeDescription"),
                Options:
                [
                    new PluginSettingOption(Reson8FillerModes.Natural, Loc.L("Settings.FillerModeNatural")),
                    new PluginSettingOption(Reson8FillerModes.Clean, Loc.L("Settings.FillerModeClean")),
                    new PluginSettingOption(Reson8FillerModes.Verbatim, Loc.L("Settings.FillerModeVerbatim")),
                ],
                Kind: PluginSettingKind.Dropdown),
            new(
                Key: BiasStrengthSettingName,
                Label: Loc.L("Settings.BiasStrength"),
                Description: Loc.L("Settings.BiasStrengthDescription", FormatBiasStrength(DefaultBiasStrength, CultureInfo.CurrentCulture)),
                Options: BiasStrengthOptions(),
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

    // A saved custom model hidden by a connection change stays listed so the choice is visible.
    private List<PluginSettingOption> ModelOptions()
    {
        var options = TranscriptionModels.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList();
        if (options.All(o => o.Value != _selectedModelId))
            options.Add(new PluginSettingOption(_selectedModelId, Loc.L("Settings.UnavailableModelOption", _selectedModelId)));
        return options;
    }

    // Steps of 0.05; the default goes first and sends nothing.
    private List<PluginSettingOption> BiasStrengthOptions() =>
    [
        new(DefaultBiasChoice, Loc.L("Settings.BiasStrengthDefault", FormatBiasStrength(DefaultBiasStrength, CultureInfo.CurrentCulture))),
        .. Enumerable.Range(0, 21)
            .Select(step => step * 0.05)
            .Where(value => Math.Abs(value - DefaultBiasStrength) >= 0.001)
            .Select(value => new PluginSettingOption(
                FormatBiasStrength(value, CultureInfo.InvariantCulture),
                FormatBiasStrength(value, CultureInfo.CurrentCulture))),
    ];

    private static string FormatBiasStrength(double value, IFormatProvider culture) => value.ToString("0.00", culture);

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                ApiKeySecretName => ApiKey,
                SelectedModelSettingName => _selectedModelId,
                CustomBaseUrlSettingName => CustomBaseUrl == DefaultBaseUrl ? null : CustomBaseUrl,
                CustomAuthHeaderSettingName => CustomAuthHeader == DefaultAuthHeader ? null : CustomAuthHeader,
                FillerModeSettingName => FillerMode,
                BiasStrengthSettingName => BiasStrength is { } bias ? FormatBiasStrength(bias, CultureInfo.InvariantCulture) : DefaultBiasChoice,
                _ => null,
            });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
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
            case FillerModeSettingName:
                SetFillerMode(value);
                break;
            case BiasStrengthSettingName:
                SetBiasStrength(value);
                break;
        }
    }

    internal string FillerMode { get; private set; } = Reson8FillerModes.Natural;

    internal double? BiasStrength { get; private set; }

    internal void SetFillerMode(string? mode)
    {
        var normalized = Reson8FillerModes.Normalize(mode);
        lock (_catalogLock)
        {
            _host?.SetSetting(FillerModeSettingName, normalized);
            FillerMode = normalized;
        }
    }

    internal void SetBiasStrength(string? value)
    {
        var normalized = ParseBiasStrength(value);
        lock (_catalogLock)
        {
            _host?.SetSetting(BiasStrengthSettingName, normalized is { } bias ? FormatBiasStrength(bias, CultureInfo.InvariantCulture) : null);
            BiasStrength = normalized;
        }
    }

    // Null (Reson8's default) unless a strength from 0 to 1 other than the default.
    internal static double? ParseBiasStrength(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && Math.Round(parsed, 2) is var strength and >= 0 and <= 1
        && Math.Abs(strength - DefaultBiasStrength) >= 0.001
            ? strength
            : null;

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
            var catalogChanged = ChangeConnection(() => ApiKey = normalized);

            if (wasConfigured == IsConfigured && !catalogChanged)
                hostToNotify = null;
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        hostToNotify?.NotifyCapabilitiesChanged();
    }

    // Saved as typed so the user can correct it; requests and Validate report an invalid value.
    internal void SetCustomBaseUrl(string? url)
    {
        var normalized = NormalizeBaseUrl(url);
        if (ChangeConnection(() =>
            {
                _host?.SetSetting(CustomBaseUrlSettingName, normalized == DefaultBaseUrl ? null : normalized);
                CustomBaseUrl = normalized;
            }))
        {
            _host?.NotifyCapabilitiesChanged();
        }
    }

    internal void SetCustomAuthHeader(string? header)
    {
        var normalized = NormalizeAuthHeader(header);
        if (ChangeConnection(() =>
            {
                _host?.SetSetting(CustomAuthHeaderSettingName, normalized == DefaultAuthHeader ? null : normalized);
                CustomAuthHeader = normalized;
            }))
        {
            _host?.NotifyCapabilitiesChanged();
        }
    }

    internal static Uri BuildPrerecordedUri(
        string baseUrl,
        string? modelId,
        string? language,
        IReadOnlyList<string>? phrases = null,
        Reson8QueryOptions? options = null)
    {
        var query = BuildQuery(modelId, language, phrases, options, includeWords: options?.IncludeWords == true);
        return new Uri($"{NormalizeBaseUrl(baseUrl)}/v1/speech-to-text/prerecorded?{string.Join("&", query)}");
    }

    // Parameters both endpoints take. A listed model goes as model, a custom one as custom_model_id.
    internal static List<string> BuildQuery(
        string? modelId,
        string? language,
        IReadOnlyList<string>? phrases,
        Reson8QueryOptions? options,
        bool includeWords)
    {
        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1",
            "include_language=true",
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language={Uri.EscapeDataString(language.Trim())}");

        var customModel = false;
        if (options?.Model is { } listedModel)
        {
            query.Add($"model={Uri.EscapeDataString(listedModel)}");
        }
        else if (!string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId.Trim())}");
            customModel = true;
        }

        if (phrases is { Count: > 0 })
            query.Add($"phrases={EncodePhrases(phrases)}");

        if (options?.FillerMode is { } fillerMode && fillerMode != Reson8FillerModes.Natural)
            query.Add($"filler_mode={fillerMode}");

        // Only phrases and custom models steer recognition, so the strength goes only with them.
        if (options?.BiasStrength is { } bias && (customModel || phrases is { Count: > 0 }))
            query.Add($"bias_strength={FormatBiasStrength(bias, CultureInfo.InvariantCulture)}");

        if (!includeWords)
            return query;

        query.Add("include_timestamps=true");
        query.Add("include_words=true");
        return query;
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(
        string json,
        string? fallbackLanguage,
        int pcm16ByteLength)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = GetString(root, "text")?.Trim() ?? "";
        var language = NonEmpty(GetString(root, "language"))
            ?? NonEmpty(GetString(root, "detected_language"))
            ?? fallbackLanguage;
        return new PluginTranscriptionResult(text, language, PcmDurationSeconds(pcm16ByteLength), NoSpeechProbability: null)
        {
            Segments = Reson8Transcript.SentenceSegments(Reson8Transcript.Words(root), text),
        };
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

    internal static string EncodePhrases(IReadOnlyList<string> phrases) =>
        Uri.EscapeDataString(string.Join(",", phrases));

    // Dictionary terms aren't sent with a custom model: Reson8 does not document combining phrases
    // with one, and it already carries its own. Rumble-2-Flash rejects phrases.
    private static IReadOnlyList<string> PhrasesFor(string? prompt, Reson8Connection connection) =>
        connection.IsCustomModel || !Reson8Models.TakesParameter(connection.Model, "phrases") ? [] : PhrasesFor(prompt);

    internal static IReadOnlyList<string> PhrasesFor(string? prompt)
    {
        var terms = PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms;

        var phrases = new List<string>();
        var queryLength = 0;
        foreach (var term in PluginDictionaryTerms.Clip(terms, s_phrasesBudget))
        {
            // Reson8 splits phrases on commas, so such a term would arrive as several.
            if (term.Contains(','))
                continue;

            var nextLength = queryLength + (phrases.Count == 0 ? 0 : 3) + Uri.EscapeDataString(term).Length;
            if (nextLength > MaxPhrasesQueryLength)
                break;

            phrases.Add(term);
            queryLength = nextLength;
        }

        return phrases;
    }

    // A comma-separated list restricts Reson8's per-utterance detection to those languages.
    internal static string? LanguageList(IReadOnlyList<string> languageHints)
    {
        var languages = languageHints
            .Select(NormalizeLanguage)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return languages.Count == 0 ? null : string.Join(",", languages);
    }

    // The host sends a lone hint through these overloads only under automatic detection; as
    // Reson8's language it would force that language, so detection stays open instead.
    internal static string? AdvisoryLanguageList(IReadOnlyList<string> languageHints) =>
        LanguageList(languageHints) is { } list && list.Contains(',') ? list : null;

    private static string? PinnedLanguage(string? language) =>
        language is not null && !language.Contains(',') ? language : null;

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

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private string FormatApiError(HttpStatusCode statusCode, string json, bool customModel)
    {
        var message = ExtractApiError(json);
        return statusCode switch
        {
            HttpStatusCode.BadRequest => $"Reson8 rejected the request: {message}",
            HttpStatusCode.Unauthorized => Loc.L("Settings.InvalidApiKey"),
            HttpStatusCode.PaymentRequired => $"Reson8 credit limit reached: {message}",
            HttpStatusCode.NotFound => customModel ? $"Reson8 custom model not found: {message}" : $"Reson8 model not found: {message}",
            HttpStatusCode.RequestEntityTooLarge => $"Reson8 file too large: {message}",
            HttpStatusCode.TooManyRequests => $"Reson8 rate limit exceeded: {message}",
            HttpStatusCode.InternalServerError => $"Reson8 server error: {message}",
            _ => $"Reson8 API error {(int)statusCode}: {message}",
        };
    }

    private static double PcmDurationSeconds(int pcm16ByteLength) =>
        pcm16ByteLength <= 0 ? 0 : pcm16ByteLength / 2.0 / 16000.0;

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
