using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

public sealed class OpenAiPlugin
    : ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        ITtsProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.openai.com";
    private const string ApiKeySecretName = "api-key";
    private const string SelectedModelSettingName = "selectedModel";
    private const string SelectedLlmModelSettingName = "selectedLLMModel";
    private const string ReasoningEffortSettingName = "reasoningEffort";
    private const string SelectedVoiceSettingName = "selectedVoice";
    private const string TtsInstructionsSettingName = "ttsInstructions";
    private const string FetchedLlmModelsSettingName = "fetchedLLMModels";
    private const string AuthModeSettingName = "authMode";
    private const string ForgetChatGptLoginSettingName = "forgetChatGptLogin";
    private const string OAuthAccessTokenSecretName = "oauth-access-token";
    private const string OAuthRefreshTokenSecretName = "oauth-refresh-token";
    private const string OAuthIdTokenSecretName = "oauth-id-token";
    private const string OAuthAccountIdSettingName = "oauthAccountID";
    private const string OAuthPlanTypeSettingName = "oauthPlanType";
    private const string OAuthExpiresAtSettingName = "oauthExpiresAt";
    private const string TemperatureModeSettingName = "llmTemperatureMode";
    private const string TemperatureValueSettingName = "llmTemperatureValue";
    private const string TemperatureModeProviderDefault = "providerDefault";
    private const string TemperatureModeCustom = "custom";

    private readonly HttpClient _httpClient;
    private readonly Func<byte[], ITtsPlaybackSession> _ttsPlaybackFactory;
    private readonly Func<bool> _ttsPlaybackAvailableProbe;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string _selectedResponseFormat = "verbose_json";
    private string? _selectedVoiceId;
    private string _ttsInstructions = "";
    private string _reasoningEffort = "medium";
    private List<OpenAiFetchedModel> _fetchedLlmModels = [];
    private OpenAiAuthMode _authMode = OpenAiAuthMode.ApiKey;
    private string? _selectedLlmModelId;
    private string? _oauthAccessToken;
    private string? _oauthRefreshToken;
    private string? _oauthIdToken;
    private string? _oauthAccountId;
    private string? _oauthPlanType;
    private DateTimeOffset? _oauthExpiresAt;
    private bool _forgetChatGptLogin;
    private string _temperatureMode = TemperatureModeProviderDefault;
    private double _temperatureValue = 0.3;
    private bool _streamResponses = true;

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-1", "Whisper 1", "whisper-1", "verbose_json", SupportsTranslation: true),
        new(
            "gpt-4o-transcribe",
            "GPT-4o Transcribe",
            "gpt-4o-transcribe",
            "json",
            SupportsTranslation: false
        ),
        new(
            "gpt-4o-mini-transcribe",
            "GPT-4o Mini Transcribe",
            "gpt-4o-mini-transcribe",
            "json",
            SupportsTranslation: false
        ),
        new(
            OpenAiRealtimeStreamingSession.ModelId,
            "GPT Realtime Whisper",
            OpenAiRealtimeStreamingSession.ModelId,
            "json",
            SupportsTranslation: false,
            SupportsStreaming: true
        ),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new("gpt-5.5", "GPT-5.5"),
        new("gpt-4.1-nano", "GPT-4.1 Nano"),
        new("gpt-4.1-mini", "GPT-4.1 Mini"),
        new("gpt-4.1", "GPT-4.1"),
        new("gpt-4o", "GPT-4o"),
        new("gpt-4o-mini", "GPT-4o Mini"),
        new("o4-mini", "o4-mini"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> ChatGptModels =
    [
        new("gpt-5.5", "GPT-5.5"),
        new("gpt-5.4", "GPT-5.4"),
        new("gpt-5.4-mini", "GPT-5.4 Mini"),
        new("gpt-5.4-nano", "GPT-5.4 Nano"),
        new("gpt-5.3-codex", "GPT-5.3 Codex"),
        new("gpt-5.3-codex-spark", "GPT-5.3 Codex Spark"),
        new("gpt-5.2", "GPT-5.2"),
        new("gpt-5.2-codex", "GPT-5.2 Codex"),
        new("gpt-5.1-codex", "GPT-5.1 Codex"),
        new("gpt-5.1-codex-max", "GPT-5.1 Codex Max"),
        new("gpt-5.1-codex-mini", "GPT-5.1 Codex Mini"),
    ];

    public OpenAiPlugin()
        : this(CreateHttpClient())
    {
    }

    internal OpenAiPlugin(
        HttpClient httpClient,
        Func<byte[], ITtsPlaybackSession>? ttsPlaybackFactory = null,
        Func<bool>? ttsPlaybackAvailableProbe = null)
    {
        _httpClient = httpClient;
        _ttsPlaybackFactory = ttsPlaybackFactory
            ?? (pcm => OpenAiPcmTtsPlaybackSession.Create(pcm, OpenAiTtsConfiguration.SampleRate));
        _ttsPlaybackAvailableProbe = ttsPlaybackAvailableProbe
            ?? OpenAiPcmTtsPlaybackSession.IsPlaybackAvailable;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openai";
    public string PluginName => "OpenAI / ChatGPT";
    public string PluginVersion => "1.2.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _oauthAccessToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthAccessTokenSecretName));
        _oauthRefreshToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthRefreshTokenSecretName));
        _oauthIdToken = NormalizeApiKey(await host.LoadSecretAsync(OAuthIdTokenSecretName));
        _authMode = OpenAiAuthModeExtensions.Parse(host.GetSetting<string>(AuthModeSettingName));
        _selectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        _ttsInstructions = host.GetSetting<string>(TtsInstructionsSettingName) ?? "";
        _reasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        _fetchedLlmModels = host.GetSetting<List<OpenAiFetchedModel>>(FetchedLlmModelsSettingName) ?? [];
        _oauthAccountId = host.GetSetting<string>(OAuthAccountIdSettingName);
        _oauthPlanType = host.GetSetting<string>(OAuthPlanTypeSettingName);
        _oauthExpiresAt = LoadExpiresAt(host);
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingName));
        _temperatureValue = NormalizeTemperatureValue(host.GetSetting<double?>(TemperatureValueSettingName));
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;

        SelectModelCore(
            host.GetSetting<string>(SelectedModelSettingName) ?? TranscriptionModelEntries[0].Id,
            persist: false);
        NormalizeSelectedLlmModel(persist: false);
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "openai";
    public string ProviderDisplayName => "OpenAI / ChatGPT";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        TranscriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList();

    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation =>
        IsConfigured && SelectedModelEntry is { SupportsTranslation: true };

    // Realtime streaming uses an API-key-authenticated WebSocket. ChatGPT
    // OAuth tokens are scoped for the consumer chat backend and 401 at
    // wss://api.openai.com/v1/realtime, so we gate streaming off when the
    // user is in OAuth mode even with the realtime model selected.
    public bool SupportsStreaming =>
        IsConfigured
        && _authMode != OpenAiAuthMode.ChatGpt
        && SelectedModelEntry is { SupportsStreaming: true };

    public void SelectModel(string modelId) => SelectModelCore(modelId, persist: true);

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException(
                "Plugin not configured. API key and model required."
            );

        if (_selectedModelId == OpenAiRealtimeStreamingSession.ModelId)
        {
            if (translate)
                throw new InvalidOperationException(
                    "GPT Realtime Whisper does not support translation."
                );

            return await OpenAiRealtimeStreamingSession.TranscribeWavAsync(
                _apiKey!,
                wavAudio,
                NormalizeLanguage(language),
                prompt,
                ct
            );
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            _selectedApiModelName,
            wavAudio,
            NormalizeLanguage(language),
            translate,
            _selectedResponseFormat,
            ct,
            prompt
        );
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        if (_authMode == OpenAiAuthMode.ChatGpt)
            throw new InvalidOperationException(
                "OpenAI realtime streaming requires an API key. "
                + "ChatGPT login can't authenticate the realtime endpoint."
            );
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));
        if (_selectedModelId != OpenAiRealtimeStreamingSession.ModelId)
            throw new NotSupportedException(
                "Select GPT Realtime Whisper to use OpenAI realtime streaming."
            );

        return await OpenAiRealtimeStreamingSession.ConnectAsync(
            _apiKey!,
            NormalizeLanguage(language),
            prompt: null,
            useServerVad: true,
            ct
        );
    }

    // ILlmProviderPlugin

    public string ProviderName => "OpenAI";

    public bool IsAvailable => _authMode switch
    {
        OpenAiAuthMode.ChatGpt => HasChatGptCredentials,
        _ => IsConfigured,
    };

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _authMode == OpenAiAuthMode.ChatGpt
            ? ChatGptModels
            : _fetchedLlmModels.Count > 0
                ? _fetchedLlmModels.Select(model => new PluginModelInfo(model.Id, model.Id)).ToList()
                : FallbackLlmModels;

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels[0].Id
            : model;

        if (_authMode == OpenAiAuthMode.ChatGpt)
        {
            var accessToken = await ValidOAuthAccessTokenAsync(ct);
            var client = new OpenAiChatGptClient(_httpClient, accessToken, _oauthAccountId);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
                ct);
        }

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        if (UsesResponsesApi(modelId))
        {
            var client = new OpenAiResponsesClient(_httpClient, BaseUrl, _apiKey!);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                SupportsReasoningEffort(modelId) ? MapApiReasoningEffort(_reasoningEffort) : null,
                ct);
        }

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
            temperature: ResolvedTemperature(modelId)
        );
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        var modelId = string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModelId ?? SupportedModels[0].Id
            : model;

        // Self-gated per the C7 per-provider toggle. Also bulk-yield the
        // ChatGPT-OAuth and Responses-API sub-paths: only /v1/chat/completions has
        // a streaming reader so far (the shared helper). The other two stay
        // byte-identical to ProcessAsync — see the C7 Phase 3 doc's scope note.
        if (!_streamResponses
            || _authMode == OpenAiAuthMode.ChatGpt
            || UsesResponsesApi(modelId))
        {
            yield return await ProcessAsync(systemPrompt, userText, modelId, ct);
            yield break;
        }

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: SupportsReasoningEffort(modelId) ? _reasoningEffort : null,
            temperature: ResolvedTemperature(modelId)
        );

        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
    }

    // ITtsProviderPlugin

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => OpenAiTtsConfiguration.AvailableVoices;

    public string? SelectedVoiceId => _selectedVoiceId ?? OpenAiTtsConfiguration.DefaultVoiceId;

    public string? SettingsSummary
    {
        get
        {
            var voice = AvailableVoices.FirstOrDefault(v => v.Id == SelectedVoiceId)?.DisplayName
                ?? OpenAiTtsConfiguration.DefaultVoiceId;
            return $"Voice: {voice}; OpenAI";
        }
    }

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return OpenAiInactiveTtsPlaybackSession.Instance;

        // The OpenAI speech endpoint is a paid request. With no audio player on
        // PATH the synthesized PCM could only be discarded (OpenAiPcmTtsPlaybackSession
        // would return the inactive sentinel), so skip the request entirely.
        if (!_ttsPlaybackAvailableProbe())
        {
            _host?.Log(
                PluginLogLevel.Warning,
                "Skipping OpenAI TTS request: no audio player (paplay/aplay) found on PATH.");
            return OpenAiInactiveTtsPlaybackSession.Instance;
        }

        using var httpRequest = CreateTtsRequest(text);
        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, httpRequest, ct);
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        return _ttsPlaybackFactory(pcm);
    }

    // LLM model catalog

    internal OpenAiAuthMode AuthMode => _authMode;

    internal bool HasChatGptCredentials =>
        !string.IsNullOrWhiteSpace(_oauthRefreshToken)
        || !string.IsNullOrWhiteSpace(_oauthAccessToken);

    internal string? ChatGptPlanType => _oauthPlanType;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal string ReasoningEffort => _reasoningEffort;
    internal string TtsInstructions => _ttsInstructions;
    internal string TemperatureMode => _temperatureMode;
    internal double TemperatureValue => _temperatureValue;

    internal static bool UsesResponsesApi(string modelId)
    {
        // Reasoning-capable models do not accept the legacy /v1/chat/completions
        // shape (temperature/max_tokens, no reasoning payload). Route them to
        // /v1/responses instead — upstream only matched gpt-5* here, so
        // o-series fallbacks like o4-mini silently failed at runtime.
        var lowered = modelId.ToLowerInvariant();
        return lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal);
    }

    internal static string? MapApiReasoningEffort(string? effort) =>
        // OpenAI's /v1/responses accepts low/medium/high (and newer "minimal").
        // "xhigh" is a Codex-CLI internal level preserved for ChatGPT-mode
        // requests; mapping it to "high" here keeps API-key Responses calls
        // valid for users who picked "X High" in the dropdown.
        effort == "xhigh" ? "high" : effort;

    internal static bool SupportsReasoningEffort(string modelId)
    {
        var lowered = modelId.ToLowerInvariant();
        return lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal)
            || lowered.Contains("codex", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the chat-completion body field used to cap output tokens.
    ///     Newer GPT-5 / o-series models reject the legacy <c>max_tokens</c>
    ///     and require <c>max_completion_tokens</c>; everything else keeps
    ///     <c>max_tokens</c>.
    /// </summary>
    internal static string OutputTokenParameter(string modelId)
    {
        var lowered = modelId.ToLowerInvariant();
        if (lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal))
        {
            return "max_completion_tokens";
        }

        return "max_tokens";
    }

    /// <summary>
    ///     Whether the model accepts a user-supplied <c>temperature</c> parameter
    ///     in chat-completion mode. GPT-5 with reasoning_effort set does not.
    /// </summary>
    internal static bool SupportsCustomTemperature(string modelId, string? reasoningEffort) =>
        ChatCompletionTemperature(modelId, reasoningEffort) is not null;

    /// <summary>
    ///     Provider-default chat-completion temperature for the given model.
    ///     Returns <c>null</c> for GPT-5 with reasoning_effort set (the model
    ///     rejects the field outright in that mode); otherwise 0.3 — the value
    ///     upstream picked when surfacing the setting to users.
    /// </summary>
    internal static double? ChatCompletionTemperature(string modelId, string? reasoningEffort)
    {
        var lowered = modelId.ToLowerInvariant();
        if (lowered.StartsWith("gpt-5", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return 0.3;
    }

    internal static string NormalizeTemperatureMode(string? mode) =>
        mode == TemperatureModeCustom ? TemperatureModeCustom : TemperatureModeProviderDefault;

    internal static double NormalizeTemperatureValue(double? value)
    {
        // `Math.Clamp(double.NaN, …)` returns NaN unchanged (IEEE 754: every
        // NaN comparison is false, so the min/max checks short-circuit). A
        // persisted NaN would later throw inside System.Text.Json when the
        // chat-completion body is serialized — and re-throw on every activate
        // that loads the setting. Reject non-finite inputs up-front so a
        // corrupted config can't poison the runtime.
        if (value is null || !double.IsFinite(value.Value))
            return 0.3;

        return Math.Clamp(value.Value, 0.0, 2.0);
    }

    internal async Task<IReadOnlyList<PluginModelInfo>> RefreshAvailableLlmModelsAsync(
        CancellationToken ct = default)
    {
        // ChatGPT-login mode uses the static ChatGptModels catalog and has no
        // /v1/models endpoint to refresh from — short-circuit to keep the
        // selection normalized without burning a (failing) HTTP call.
        if (_authMode == OpenAiAuthMode.ChatGpt)
        {
            NormalizeSelectedLlmModel(persist: true);
            return SupportedModels;
        }

        var models = await FetchLlmModelsAsync(ct);
        if (models.Count == 0)
            return [];

        _fetchedLlmModels = models.ToList();
        _host?.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
        // Upstream's verbatim version skipped this — if the previously
        // selected model isn't in the freshly fetched catalog, _selectedLlmModelId
        // dangles and ProcessAsync's default-model fallback would send an
        // unsupported model ID until the user re-saved the dropdown. Mirrors
        // the xAI plugin's SetFetchedLlmModels normalize-on-refresh behavior.
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
        return SupportedModels;
    }

    internal async Task<IReadOnlyList<OpenAiFetchedModel>> FetchLlmModelsAsync(
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenAiModelsResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return decoded?.Data
                .Where(model => IsChatModel(model.Id))
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .ToList()
                ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    internal static bool IsChatModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        // OpenAI ships bare o-series GA model IDs (`o1`, `o3`) alongside the
        // dashed variants. Upstream's verbatim filter required the trailing
        // hyphen and dropped the bare IDs from the fetched catalog even
        // though UsesResponsesApi already routes them correctly.
        var hasChatPrefix = lowered.StartsWith("gpt-", StringComparison.Ordinal)
            || lowered.StartsWith("o1", StringComparison.Ordinal)
            || lowered.StartsWith("o3", StringComparison.Ordinal)
            || lowered.StartsWith("o4", StringComparison.Ordinal)
            || lowered.StartsWith("chatgpt-", StringComparison.Ordinal);
        if (!hasChatPrefix)
            return false;

        string[] excludeSuffixes = ["-tts", "-embedding"];
        string[] excludeContains =
        [
            "dall-e",
            "whisper",
            "transcribe",
            "tts-",
            "text-embedding",
            "audio",
            "realtime",
            "gpt-image",
            "-search"
        ];
        return !excludeSuffixes.Any(suffix => lowered.EndsWith(suffix, StringComparison.Ordinal))
            && !excludeContains.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal));
    }

    internal void SetAuthMode(OpenAiAuthMode mode)
    {
        if (_authMode == mode)
            return;

        _authMode = mode;
        _host?.SetSetting(AuthModeSettingName, mode.ToStorageValue());
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = (SupportedModels.Count > 0 ? SupportedModels[0] : null)?.Id ?? modelId;

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string effort)
    {
        _reasoningEffort = NormalizeReasoningEffort(effort);
        _host?.SetSetting(ReasoningEffortSettingName, _reasoningEffort);
    }

    internal void SetTemperatureMode(string? mode)
    {
        _temperatureMode = NormalizeTemperatureMode(mode);
        _host?.SetSetting(TemperatureModeSettingName, _temperatureMode);
    }

    internal void SetTemperatureValue(double value)
    {
        _temperatureValue = NormalizeTemperatureValue(value);
        _host?.SetSetting(TemperatureValueSettingName, _temperatureValue);
    }

    // ChatGPT OAuth login

    internal async Task LoginWithChatGptInBrowserAsync(CancellationToken ct = default)
    {
        var state = OpenAiOAuthClient.RandomState();
        var pkce = OpenAiOAuthClient.GeneratePkceCodes();
        await using var server = new OpenAiLoopbackOAuthServer(state);
        server.Start();

        var authUri = OpenAiOAuthClient.BuildAuthorizeUri(state, pkce);
        Process.Start(new ProcessStartInfo
        {
            FileName = authUri.ToString(),
            UseShellExecute = true
        });

        var code = await server.WaitForCodeAsync(ct);
        var tokens = await OpenAiOAuthClient.ExchangeAuthorizationCodeAsync(_httpClient, code, pkce, ct);
        await StoreOAuthTokensAsync(tokens, preferredAccountId: null);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
    }

    internal async Task ImportExistingLoginAsync(string? authFilePath = null)
    {
        authFilePath ??= Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");

        if (!File.Exists(authFilePath))
            throw new FileNotFoundException("No existing login file was found.", authFilePath);

        var json = await File.ReadAllTextAsync(authFilePath);
        var store = JsonSerializer.Deserialize<OpenAiExistingLoginStore>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Existing login file could not be parsed.");

        var tokens = new OpenAiOAuthTokenResponse(
            store.Tokens.IdToken,
            store.Tokens.AccessToken,
            store.Tokens.RefreshToken,
            ExpiresIn: null);
        await StoreOAuthTokensAsync(tokens, store.Tokens.AccountId);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
    }

    internal async Task ClearChatGptLoginAsync()
    {
        _oauthAccessToken = null;
        _oauthRefreshToken = null;
        _oauthIdToken = null;
        _oauthAccountId = null;
        _oauthPlanType = null;
        _oauthExpiresAt = null;

        if (_host is not null)
        {
            await _host.DeleteSecretAsync(OAuthAccessTokenSecretName);
            await _host.DeleteSecretAsync(OAuthRefreshTokenSecretName);
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
            _host.SetSetting<string?>(OAuthAccountIdSettingName, null);
            _host.SetSetting<string?>(OAuthPlanTypeSettingName, null);
            _host.SetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName, null);
            _host.NotifyCapabilitiesChanged();
        }
    }

    // API key / settings management

    internal string? ApiKey => _apiKey;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(_apiKey, normalized, StringComparison.Ordinal);

        _apiKey = normalized;
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

    internal void SetTtsInstructions(string instructions)
    {
        _ttsInstructions = instructions.Trim();
        _host?.SetSetting(TtsInstructionsSettingName, _ttsInstructions);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private TranscriptionModelEntry? SelectedModelEntry =>
        TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId);

    private void SelectModelCore(string modelId, bool persist)
    {
        var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? TranscriptionModelEntries[0];
        _selectedModelId = entry.Id;
        _selectedApiModelName = entry.ApiModelName;
        _selectedResponseFormat = entry.ResponseFormat;

        if (persist)
            _host?.SetSetting(SelectedModelSettingName, entry.Id);
    }

    private HttpRequestMessage CreateTtsRequest(string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = OpenAiJson.CreateJsonContent(
            OpenAiTtsConfiguration.CreateRequestBody(text, SelectedVoiceId, _ttsInstructions));
        return request;
    }

    private async Task<string> ValidOAuthAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_oauthAccessToken)
            && _oauthExpiresAt is { } expiresAt
            && expiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            return _oauthAccessToken;
        }

        if (string.IsNullOrWhiteSpace(_oauthRefreshToken))
            throw new InvalidOperationException(Loc.L("Settings.ChatGptLoginNotConfigured"));

        var refreshed = await OpenAiOAuthClient.RefreshTokenAsync(_httpClient, _oauthRefreshToken, ct);
        await StoreOAuthTokensAsync(refreshed, _oauthAccountId);
        return refreshed.AccessToken;
    }

    private async Task StoreOAuthTokensAsync(OpenAiOAuthTokenResponse tokens, string? preferredAccountId)
    {
        var metadata = OpenAiOAuthClient.ExtractMetadata(tokens, preferredAccountId);
        _oauthAccessToken = tokens.AccessToken;
        // RFC 6749 §6: a refresh response MAY omit `refresh_token`, meaning
        // "keep using the previously issued one". Unconditionally assigning
        // tokens.RefreshToken here would null out the only usable refresh
        // token on the first refresh that doesn't rotate it.
        var effectiveRefreshToken = string.IsNullOrEmpty(tokens.RefreshToken)
            ? _oauthRefreshToken
            : tokens.RefreshToken;
        _oauthRefreshToken = effectiveRefreshToken;
        _oauthIdToken = tokens.IdToken;
        _oauthAccountId = metadata.AccountId;
        _oauthPlanType = metadata.PlanType;
        _oauthExpiresAt = metadata.ExpiresAt;

        if (_host is null)
            return;

        await _host.StoreSecretAsync(OAuthAccessTokenSecretName, tokens.AccessToken);
        if (!string.IsNullOrEmpty(effectiveRefreshToken))
            await _host.StoreSecretAsync(OAuthRefreshTokenSecretName, effectiveRefreshToken);
        if (string.IsNullOrWhiteSpace(tokens.IdToken))
            await _host.DeleteSecretAsync(OAuthIdTokenSecretName);
        else
            await _host.StoreSecretAsync(OAuthIdTokenSecretName, tokens.IdToken);
        _host.SetSetting(OAuthAccountIdSettingName, _oauthAccountId);
        _host.SetSetting(OAuthPlanTypeSettingName, _oauthPlanType);
        _host.SetSetting(OAuthExpiresAtSettingName, _oauthExpiresAt);
        NormalizeSelectedLlmModel(persist: true);
        _host.NotifyCapabilitiesChanged();
    }

    internal double? ResolvedTemperature(string modelId)
    {
        // When the model rejects temperature outright (e.g. GPT-5 with a
        // reasoning_effort set), honor that regardless of the user's mode —
        // sending the field would 400 the request.
        var reasoningEffort = SupportsReasoningEffort(modelId) ? _reasoningEffort : null;
        if (!SupportsCustomTemperature(modelId, reasoningEffort))
            return null;

        return _temperatureMode == TemperatureModeCustom
            ? _temperatureValue
            : ChatCompletionTemperature(modelId, reasoningEffort);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        if (_selectedLlmModelId is null
            || available.All(model => !string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
        {
            _selectedLlmModelId = available[0].Id;
        }

        // Persist even when the in-memory selection didn't change — this guards
        // against a stale-cleared setting where _selectedLlmModelId is still
        // valid but the persisted setting was lost.
        if (persist)
            _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
    }

    private static DateTimeOffset? LoadExpiresAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName);
            return value == default ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language;

    private static string NormalizeReasoningEffort(string? effort) =>
        effort is "low" or "medium" or "high" or "xhigh" ? effort : "medium";

    private static string NormalizeVoiceId(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId)
        && OpenAiTtsConfiguration.AvailableVoices.Any(v => v.Id == voiceId)
            ? voiceId
            : OpenAiTtsConfiguration.DefaultVoiceId;

    // IPluginSettingsProvider
    //
    // The Windows build exposed these via the WPF OpenAiSettingsView UserControl;
    // the fork renders settings generically from the metadata below. The fork's
    // IPluginSettingsProvider has no explicit "refresh"/"login" action, so the
    // API-key catalog fetch and the ChatGPT login flow are both driven from
    // ValidateAsync (the host's key-test entry point) — the same pattern B1
    // (xAI) used for its dynamic model/voice catalog.

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: AuthModeSettingName,
                Label: Loc.L("Settings.ConnectionMethod"),
                Description: Loc.L("Settings.ConnectionMethodDescription"),
                Options:
                [
                    new PluginSettingOption(
                        OpenAiAuthMode.ApiKey.ToStorageValue(),
                        Loc.L("Settings.ApiKey")),
                    new PluginSettingOption(
                        OpenAiAuthMode.ChatGpt.ToStorageValue(),
                        Loc.L("Settings.ChatGptLogin")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "sk-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: SelectedModelSettingName,
                Label: Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.TranscriptionModelDescription"),
                Options: TranscriptionModelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: _authMode == OpenAiAuthMode.ChatGpt
                    ? Loc.L("Settings.LlmModelDescriptionChatGpt")
                    : _fetchedLlmModels.Count > 0
                        ? Loc.L("Settings.LlmModelDescriptionFetched", _fetchedLlmModels.Count)
                        : Loc.L("Settings.LlmModelDescriptionDefault"),
                Options: SupportedModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: ReasoningEffortSettingName,
                Label: Loc.L("Settings.ReasoningEffort"),
                Description: Loc.L("Settings.ReasoningEffortDescription"),
                Options:
                [
                    new PluginSettingOption("low", Loc.L("Settings.ReasoningLow")),
                    new PluginSettingOption("medium", Loc.L("Settings.ReasoningMedium")),
                    new PluginSettingOption("high", Loc.L("Settings.ReasoningHigh")),
                    new PluginSettingOption("xhigh", Loc.L("Settings.ReasoningXHigh")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TemperatureModeSettingName,
                Label: Loc.L("Settings.Temperature"),
                Description: Loc.L("Settings.TemperatureDescription"),
                Options:
                [
                    new PluginSettingOption(
                        TemperatureModeProviderDefault,
                        Loc.L("Settings.TemperatureProviderDefault")),
                    new PluginSettingOption(
                        TemperatureModeCustom,
                        Loc.L("Settings.TemperatureCustom")),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TemperatureValueSettingName,
                Label: Loc.L("Settings.TemperatureValue"),
                Placeholder: "0.3",
                Description: Loc.L("Settings.TemperatureValueDescription"),
                Kind: PluginSettingKind.Text
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: SelectedVoiceSettingName,
                Label: Loc.L("Settings.Voice"),
                Description: Loc.L("Settings.VoiceDescription"),
                Options: AvailableVoices
                    .Select(v => new PluginSettingOption(v.Id, v.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TtsInstructionsSettingName,
                Label: Loc.L("Settings.VoiceInstructions"),
                Placeholder: Loc.L("Settings.Optional"),
                Description: Loc.L("Settings.VoiceInstructionsDescription"),
                Kind: PluginSettingKind.Multiline
            ),
            new(
                Key: ForgetChatGptLoginSettingName,
                Label: Loc.L("Settings.ForgetChatGptLogin"),
                Description: Loc.L("Settings.ForgetChatGptLoginDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                AuthModeSettingName => _authMode.ToStorageValue(),
                ApiKeySecretName => _apiKey,
                SelectedModelSettingName => _selectedModelId,
                SelectedLlmModelSettingName => _selectedLlmModelId,
                ReasoningEffortSettingName => _reasoningEffort,
                TemperatureModeSettingName => _temperatureMode,
                TemperatureValueSettingName => _temperatureValue.ToString(
                    CultureInfo.InvariantCulture),
                SelectedVoiceSettingName => _selectedVoiceId,
                TtsInstructionsSettingName => _ttsInstructions,
                ForgetChatGptLoginSettingName => _forgetChatGptLogin ? "true" : "false",
                LlmStreamingSettings.StreamResponsesSettingKey => _streamResponses ? "true" : "false",
                _ => null,
            }
        );

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case AuthModeSettingName:
                SetAuthMode(OpenAiAuthModeExtensions.Parse(value));
                break;
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case SelectedModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case SelectedLlmModelSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectLlmModel(value);
                break;
            case ReasoningEffortSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SetReasoningEffort(value);
                break;
            case TemperatureModeSettingName:
                SetTemperatureMode(value);
                break;
            case TemperatureValueSettingName:
                // `double.TryParse(..., NumberStyles.Float, ...)` accepts
                // "NaN" / "Infinity" / "-Infinity" — reject them before they
                // can reach the persisted setting (System.Text.Json throws on
                // non-finite doubles by default, which would break both save
                // and the next activate).
                if (double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedTemperature)
                    && double.IsFinite(parsedTemperature))
                {
                    SetTemperatureValue(parsedTemperature);
                }
                break;
            case SelectedVoiceSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectVoice(value);
                break;
            case TtsInstructionsSettingName:
                SetTtsInstructions(value ?? string.Empty);
                break;
            case ForgetChatGptLoginSettingName:
                _forgetChatGptLogin = ParseBool(value);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    internal void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default) =>
        _authMode == OpenAiAuthMode.ChatGpt
            ? await ValidateChatGptAsync(ct)
            : await ValidateApiKeyModeAsync(ct);

    private async Task<PluginSettingsValidationResult?> ValidateApiKeyModeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var models = await RefreshAvailableLlmModelsAsync(ct);
        return new PluginSettingsValidationResult(
            true,
            models.Count > 0
                ? Loc.L("Settings.ApiKeyValidFetched", models.Count)
                : Loc.L("Settings.ApiKeyValidDefault")
        );
    }

    private async Task<PluginSettingsValidationResult?> ValidateChatGptAsync(CancellationToken ct)
    {
        if (_forgetChatGptLogin)
        {
            await ClearChatGptLoginAsync();
            _forgetChatGptLogin = false;
            return new PluginSettingsValidationResult(true, Loc.L("Settings.ChatGptLoginRemoved"));
        }

        if (HasChatGptCredentials)
        {
            // Stored credentials might have been revoked or expired beyond refresh.
            // ValidOAuthAccessTokenAsync returns the cached access token if it's
            // still valid, otherwise hits the refresh endpoint — either way, a
            // failure means the credentials no longer work.
            try
            {
                _ = await ValidOAuthAccessTokenAsync(ct);
                return new PluginSettingsValidationResult(true, ChatGptConnectedMessage());
            }
            catch (Exception ex)
                when (ex is InvalidOperationException or HttpRequestException or JsonException)
            {
                // Don't auto-clear credentials here — HttpRequestException can also fire
                // on transient network failures where the stored tokens are still valid.
                // Surface the failure plus an explicit recovery path the user can take.
                return new PluginSettingsValidationResult(
                    false,
                    Loc.L("Settings.ChatGptLoginRefreshFailed", ex.Message));
            }
        }

        try
        {
            await ImportExistingLoginAsync();
            return new PluginSettingsValidationResult(true, ChatGptConnectedMessage());
        }
        catch (FileNotFoundException)
        {
            // No ~/.codex/auth.json — fall through to the interactive browser login.
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
        {
            return new PluginSettingsValidationResult(
                false, Loc.L("Settings.ChatGptImportFailed", ex.Message));
        }

        try
        {
            await LoginWithChatGptInBrowserAsync(ct);
            return new PluginSettingsValidationResult(true, ChatGptConnectedMessage());
        }
        catch (SocketException)
        {
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.ChatGptLoginPortInUse"));
        }
        catch (Win32Exception ex)
        {
            // Process.Start(UseShellExecute=true) launches the browser via
            // xdg-open on Linux. On headless boxes or minimal installs with no
            // default browser registered, that call throws Win32Exception and
            // would otherwise fault the settings-validation command.
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.ChatGptLoginNoBrowser", ex.Message));
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ChatGptLoginFailed", ex.Message));
        }
    }

    private string ChatGptConnectedMessage() =>
        string.IsNullOrWhiteSpace(_oauthPlanType)
            ? Loc.L("Settings.ChatGptLoginConnected")
            : Loc.L("Settings.ChatGptLoginConnectedPlan", _oauthPlanType);

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private sealed record TranscriptionModelEntry(
        string Id,
        string DisplayName,
        string ApiModelName,
        string ResponseFormat,
        bool SupportsTranslation,
        bool SupportsStreaming = false
    );

    private sealed record OpenAiModelsResponse(List<OpenAiFetchedModel> Data);
}
