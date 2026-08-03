// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

public sealed class OpenAiPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
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

    private static readonly JsonSerializerOptions s_jsonReadOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _selectedApiModelName;
    private string _selectedResponseFormat = "verbose_json";
    private string? _selectedVoiceId;
    private List<OpenAiFetchedModel> _fetchedLlmModels = [];
    private readonly SemaphoreSlim _oauthCredentialGate = new(1, 1);
    private OAuthCredentialSnapshot _oauthCredentials = OAuthCredentialSnapshot.Empty;
    private bool _forgetChatGptLogin;
    private bool _streamResponses = true;

    private static readonly IReadOnlyList<TranscriptionModelEntry> s_transcriptionModelEntries =
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

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new("gpt-5.5", "GPT-5.5"),
        new("gpt-4.1-nano", "GPT-4.1 Nano"),
        new("gpt-4.1-mini", "GPT-4.1 Mini"),
        new("gpt-4.1", "GPT-4.1"),
        new("gpt-4o", "GPT-4o"),
        new("gpt-4o-mini", "GPT-4o Mini"),
        new("o4-mini", "o4-mini"),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> s_chatGptModels =
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

    internal OpenAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openai";
    public string PluginName => "OpenAI / ChatGPT";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));

        await _oauthCredentialGate.WaitAsync();
        try
        {
            Volatile.Write(
                ref _oauthCredentials,
                new OAuthCredentialSnapshot(
                    NormalizeApiKey(await host.LoadSecretAsync(OAuthAccessTokenSecretName)),
                    NormalizeApiKey(await host.LoadSecretAsync(OAuthRefreshTokenSecretName)),
                    NormalizeApiKey(await host.LoadSecretAsync(OAuthIdTokenSecretName)),
                    host.GetSetting<string>(OAuthAccountIdSettingName),
                    host.GetSetting<string>(OAuthPlanTypeSettingName),
                    LoadExpiresAt(host)
                ));
        }
        finally
        {
            _oauthCredentialGate.Release();
        }

        AuthMode = OpenAiAuthModeExtensions.Parse(host.GetSetting<string>(AuthModeSettingName));
        SelectedLlmModelId = host.GetSetting<string>(SelectedLlmModelSettingName);
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        TtsInstructions = host.GetSetting<string>(TtsInstructionsSettingName) ?? "";
        ReasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingName));
        _fetchedLlmModels = host.GetSetting<List<OpenAiFetchedModel>>(FetchedLlmModelsSettingName) ?? [];
        TemperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingName));
        TemperatureValue = NormalizeTemperatureValue(host.GetSetting<double?>(TemperatureValueSettingName));
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;

        SelectModelCore(
            host.GetSetting<string>(SelectedModelSettingName) ?? s_transcriptionModelEntries[0].Id,
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
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_transcriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList();

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation =>
        IsConfigured && SelectedModelEntry is { SupportsTranslation: true };

    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    // Realtime streaming uses an API-key-authenticated WebSocket. ChatGPT
    // OAuth tokens are scoped for the consumer chat backend and 401 at
    // wss://api.openai.com/v1/realtime, so we gate streaming off when the
    // user is in OAuth mode even with the realtime model selected.
    public bool SupportsStreaming =>
        IsConfigured
        && AuthMode != OpenAiAuthMode.ChatGpt
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

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (SelectedModelId == OpenAiRealtimeStreamingSession.ModelId)
        {
            if (translate)
                throw new InvalidOperationException(
                    "GPT Realtime Whisper does not support translation."
                );

            return await OpenAiRealtimeStreamingSession.TranscribeWavAsync(
                ApiKey!,
                wavAudio,
                NormalizeLanguage(language),
                prompt,
                ct
            );
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
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
        if (AuthMode == OpenAiAuthMode.ChatGpt)
            throw new InvalidOperationException(
                "OpenAI realtime streaming requires an API key. "
                + "ChatGPT login can't authenticate the realtime endpoint."
            );
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));
        if (SelectedModelId != OpenAiRealtimeStreamingSession.ModelId)
            throw new NotSupportedException(
                "Select GPT Realtime Whisper to use OpenAI realtime streaming."
            );

        return await OpenAiRealtimeStreamingSession.ConnectAsync(
            ApiKey!,
            NormalizeLanguage(language),
            prompt: null,
            useServerVad: true,
            ct
        );
    }

    // ILlmProviderPlugin

    public string ProviderName => "OpenAI";

    public bool IsAvailable => AuthMode switch
    {
        OpenAiAuthMode.ChatGpt => HasChatGptCredentials,
        _ => IsConfigured,
    };

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        AuthMode == OpenAiAuthMode.ChatGpt
            ? s_chatGptModels
            : _fetchedLlmModels.Count > 0
                ? _fetchedLlmModels.Select(model => new PluginModelInfo(model.Id, model.Id)).ToList()
                : s_fallbackLlmModels;

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        var modelId = string.IsNullOrWhiteSpace(model)
            ? SelectedLlmModelId ?? SupportedModels[0].Id
            : model;

        if (AuthMode == OpenAiAuthMode.ChatGpt)
        {
            var credentials = await ValidOAuthCredentialsAsync(ct);
            var client = new OpenAiChatGptClient(
                _httpClient,
                credentials.AccessToken!,
                credentials.AccountId
            );
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                SupportsReasoningEffort(modelId) ? ReasoningEffort : null,
                ct);
        }

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (UsesResponsesApi(modelId))
        {
            var client = new OpenAiResponsesClient(_httpClient, BaseUrl, ApiKey!);
            return await client.ProcessAsync(
                systemPrompt,
                userText,
                modelId,
                SupportsReasoningEffort(modelId) ? MapApiReasoningEffort(ReasoningEffort) : null,
                ct);
        }

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: SupportsReasoningEffort(modelId) ? ReasoningEffort : null,
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
            ? SelectedLlmModelId ?? SupportedModels[0].Id
            : model;

        // Self-gated per the C7 per-provider toggle. Also bulk-yield the
        // ChatGPT-OAuth and Responses-API sub-paths: only /v1/chat/completions has
        // a streaming reader so far (the shared helper). The other two stay
        // byte-identical to ProcessAsync — see the C7 Phase 3 doc's scope note.
        if (!_streamResponses
            || AuthMode == OpenAiAuthMode.ChatGpt
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
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: OutputTokenParameter(modelId),
            reasoningEffort: SupportsReasoningEffort(modelId) ? ReasoningEffort : null,
            temperature: ResolvedTemperature(modelId)
        );

        await foreach (var delta in source)
            yield return delta;
    }

    // ITtsProviderPlugin

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => OpenAiTtsConfiguration.AvailableVoices;

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedVoiceId => _selectedVoiceId ?? OpenAiTtsConfiguration.DefaultVoiceId;

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
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

        var host = _host
                   ?? throw new InvalidOperationException(
                       "OpenAI plugin is not activated."
                   );
        // Paid endpoint: preflight playback before issuing a request that couldn't be played.
        if (!host.PcmPlayback.IsAvailable)
        {
            host.Log(
                PluginLogLevel.Warning,
                "Skipping OpenAI TTS request: no supported PCM audio player is available."
            );
            return OpenAiInactiveTtsPlaybackSession.Instance;
        }

        using var httpRequest = CreateTtsRequest(text);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient,
            httpRequest,
            ct
        );
        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        return await host.PcmPlayback.PlayAsync(
            new PcmPlaybackRequest(
                pcm,
                OpenAiTtsConfiguration.SampleRate,
                1,
                PcmSampleFormat.Signed16LittleEndian
            ),
            ct
        );
    }

    // LLM model catalog

    internal OpenAiAuthMode AuthMode { get; private set; } = OpenAiAuthMode.ApiKey;

    internal bool HasChatGptCredentials
    {
        get
        {
            var credentials = Volatile.Read(ref _oauthCredentials);
            return !string.IsNullOrWhiteSpace(credentials.RefreshToken)
                || !string.IsNullOrWhiteSpace(credentials.AccessToken);
        }
    }

    internal string? ChatGptPlanType => Volatile.Read(ref _oauthCredentials).PlanType;

    internal string? SelectedLlmModelId { get; private set; }

    internal string ReasoningEffort { get; private set; } = "medium";

    internal string TtsInstructions { get; private set; } = "";

    internal string TemperatureMode { get; private set; } = TemperatureModeProviderDefault;

    internal double TemperatureValue { get; private set; } = 0.3;

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
        // ChatGPT-login mode uses the static s_chatGptModels catalog and has no
        // /v1/models endpoint to refresh from — short-circuit to keep the
        // selection normalized without burning a (failing) HTTP call.
        if (AuthMode == OpenAiAuthMode.ChatGpt)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var decoded = JsonSerializer.Deserialize<OpenAiModelsResponse>(
                json,
                s_jsonReadOptions);

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
            "-search",
        ];
        return !excludeSuffixes.Any(suffix => lowered.EndsWith(suffix, StringComparison.Ordinal))
            && !excludeContains.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal));
    }

    internal void SetAuthMode(OpenAiAuthMode mode)
    {
        if (AuthMode == mode)
            return;

        AuthMode = mode;
        _host?.SetSetting(AuthModeSettingName, mode.ToStorageValue());
        NormalizeSelectedLlmModel(persist: true);
        _host?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        if (SupportedModels.All(model => !string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            modelId = (SupportedModels.Count > 0 ? SupportedModels[0] : null)?.Id ?? modelId;

        SelectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string effort)
    {
        ReasoningEffort = NormalizeReasoningEffort(effort);
        _host?.SetSetting(ReasoningEffortSettingName, ReasoningEffort);
    }

    internal void SetTemperatureMode(string? mode)
    {
        TemperatureMode = NormalizeTemperatureMode(mode);
        _host?.SetSetting(TemperatureModeSettingName, TemperatureMode);
    }

    internal void SetTemperatureValue(double value)
    {
        TemperatureValue = NormalizeTemperatureValue(value);
        _host?.SetSetting(TemperatureValueSettingName, TemperatureValue);
    }

    // ChatGPT OAuth login

    internal async Task LoginWithChatGptInBrowserAsync(CancellationToken ct = default)
    {
        var state = OpenAiOAuthClient.RandomState();
        var pkce = OpenAiOAuthClient.GeneratePkceCodes();
        await using var server = new OpenAiLoopbackOAuthServer(state);
        server.Start();

        var authUri = OpenAiOAuthClient.BuildAuthorizeUri(state, pkce);
        var launch = (
            _host
            ?? throw new InvalidOperationException("OpenAI plugin is not activated.")
        ).Processes.LaunchUri(authUri);
        if (!launch.Started)
        {
            // LaunchUri reports failure instead of throwing; re-raise as Win32Exception
            // so existing catch sites still match.
            throw new Win32Exception(
                launch.StartError ?? "Could not open the authorization page."
            );
        }

        var code = await server.WaitForCodeAsync(ct);
        var tokens = await OpenAiOAuthClient.ExchangeAuthorizationCodeAsync(_httpClient, code, pkce, ct);
        await StoreOAuthTokensAsync(tokens, preferredAccountId: null, ct: ct);
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
            s_jsonReadOptions)
            ?? throw new InvalidOperationException("Existing login file could not be parsed.");

        var tokens = new OpenAiOAuthTokenResponse(
            store.Tokens.IdToken,
            store.Tokens.AccessToken,
            store.Tokens.RefreshToken,
            ExpiresIn: null);
        await StoreOAuthTokensAsync(tokens, store.Tokens.AccountId);
        SetAuthMode(OpenAiAuthMode.ChatGpt);
    }

    internal async Task ClearChatGptLoginAsync(CancellationToken ct = default)
    {
        await _oauthCredentialGate.WaitAsync(ct);
        try
        {
            await CommitOAuthCredentialSnapshotUnderGateAsync(OAuthCredentialSnapshot.Empty);
        }
        finally
        {
            _oauthCredentialGate.Release();
        }
    }

    // API key / settings management

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
        var normalized = NormalizeApiKey(apiKey);
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

    internal void SetTtsInstructions(string instructions)
    {
        TtsInstructions = instructions.Trim();
        _host?.SetSetting(TtsInstructionsSettingName, TtsInstructions);
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
        s_transcriptionModelEntries.FirstOrDefault(m => m.Id == SelectedModelId);

    private void SelectModelCore(string modelId, bool persist)
    {
        var entry = s_transcriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? s_transcriptionModelEntries[0];
        SelectedModelId = entry.Id;
        _selectedApiModelName = entry.ApiModelName;
        _selectedResponseFormat = entry.ResponseFormat;

        if (persist)
            _host?.SetSetting(SelectedModelSettingName, entry.Id);
    }

    private HttpRequestMessage CreateTtsRequest(string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = OpenAiJson.CreateJsonContent(
            OpenAiTtsConfiguration.CreateRequestBody(text, SelectedVoiceId, TtsInstructions));
        return request;
    }

    private async Task<OAuthCredentialSnapshot> ValidOAuthCredentialsAsync(CancellationToken ct)
    {
        var credentials = Volatile.Read(ref _oauthCredentials);
        if (HasValidOAuthAccessToken(credentials))
            return credentials;

        await _oauthCredentialGate.WaitAsync(ct);
        try
        {
            // A preceding waiter may have refreshed and atomically replaced
            // the credential snapshot while this request waited for the gate.
            credentials = Volatile.Read(ref _oauthCredentials);
            if (HasValidOAuthAccessToken(credentials))
                return credentials;

            if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
                throw new InvalidOperationException(Loc.L("Settings.ChatGptLoginNotConfigured"));

            var refreshed = await OpenAiOAuthClient.RefreshTokenAsync(
                _httpClient,
                credentials.RefreshToken,
                ct);
            var refreshedCredentials = CreateOAuthCredentialSnapshot(
                refreshed,
                credentials.AccountId,
                credentials.RefreshToken);
            await CommitOAuthCredentialSnapshotUnderGateAsync(refreshedCredentials);
            return refreshedCredentials;
        }
        finally
        {
            _oauthCredentialGate.Release();
        }
    }

    private async Task StoreOAuthTokensAsync(
        OpenAiOAuthTokenResponse tokens,
        string? preferredAccountId,
        CancellationToken ct = default)
    {
        await _oauthCredentialGate.WaitAsync(ct);
        try
        {
            var currentCredentials = Volatile.Read(ref _oauthCredentials);
            var credentials = CreateOAuthCredentialSnapshot(
                tokens,
                preferredAccountId,
                currentCredentials.RefreshToken);
            await CommitOAuthCredentialSnapshotUnderGateAsync(credentials);
        }
        finally
        {
            _oauthCredentialGate.Release();
        }
    }

    private static OAuthCredentialSnapshot CreateOAuthCredentialSnapshot(
        OpenAiOAuthTokenResponse tokens,
        string? preferredAccountId,
        string? existingRefreshToken)
    {
        var metadata = OpenAiOAuthClient.ExtractMetadata(tokens, preferredAccountId);
        // RFC 6749 §6: a refresh response MAY omit `refresh_token`, meaning
        // "keep using the previously issued one". Unconditionally assigning
        // tokens.RefreshToken here would null out the only usable refresh
        // token on the first refresh that doesn't rotate it.
        var effectiveRefreshToken = string.IsNullOrEmpty(tokens.RefreshToken)
            ? existingRefreshToken
            : tokens.RefreshToken;

        return new OAuthCredentialSnapshot(
            tokens.AccessToken,
            effectiveRefreshToken,
            tokens.IdToken,
            metadata.AccountId,
            metadata.PlanType,
            metadata.ExpiresAt
        );
    }

    private async Task CommitOAuthCredentialSnapshotUnderGateAsync(
        OAuthCredentialSnapshot credentials)
    {
        Volatile.Write(ref _oauthCredentials, credentials);

        var host = _host;
        if (host is null)
            return;

        if (string.IsNullOrWhiteSpace(credentials.AccessToken))
            await host.DeleteSecretAsync(OAuthAccessTokenSecretName);
        else
            await host.StoreSecretAsync(OAuthAccessTokenSecretName, credentials.AccessToken);
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
            await host.DeleteSecretAsync(OAuthRefreshTokenSecretName);
        else
            await host.StoreSecretAsync(OAuthRefreshTokenSecretName, credentials.RefreshToken);
        if (string.IsNullOrWhiteSpace(credentials.IdToken))
            await host.DeleteSecretAsync(OAuthIdTokenSecretName);
        else
            await host.StoreSecretAsync(OAuthIdTokenSecretName, credentials.IdToken);
        host.SetSetting(OAuthAccountIdSettingName, credentials.AccountId);
        host.SetSetting(OAuthPlanTypeSettingName, credentials.PlanType);
        host.SetSetting(OAuthExpiresAtSettingName, credentials.ExpiresAt);
        NormalizeSelectedLlmModel(persist: true);
        host.NotifyCapabilitiesChanged();
    }

    private static bool HasValidOAuthAccessToken(OAuthCredentialSnapshot credentials) =>
        !string.IsNullOrWhiteSpace(credentials.AccessToken)
        && credentials.ExpiresAt is { } expiresAt
        && expiresAt > DateTimeOffset.UtcNow.AddSeconds(60);

    internal double? ResolvedTemperature(string modelId)
    {
        // When the model rejects temperature outright (e.g. GPT-5 with a
        // reasoning_effort set), honor that regardless of the user's mode —
        // sending the field would 400 the request.
        var reasoningEffort = SupportsReasoningEffort(modelId) ? ReasoningEffort : null;
        if (!SupportsCustomTemperature(modelId, reasoningEffort))
            return null;

        return TemperatureMode == TemperatureModeCustom
            ? TemperatureValue
            : ChatCompletionTemperature(modelId, reasoningEffort);
    }

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        if (SelectedLlmModelId is null
            || available.All(model => !string.Equals(model.Id, SelectedLlmModelId, StringComparison.Ordinal)))
        {
            SelectedLlmModelId = available[0].Id;
        }

        // Persist even when the in-memory selection didn't change — this guards
        // against a stale-cleared setting where _selectedLlmModelId is still
        // valid but the persisted setting was lost.
        if (persist)
            _host?.SetSetting(SelectedLlmModelSettingName, SelectedLlmModelId);
    }

    private static DateTimeOffset? LoadExpiresAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(OAuthExpiresAtSettingName);
            return value;
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
        string.IsNullOrWhiteSpace(language) ? null : language;

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
                Options: s_transcriptionModelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: AuthMode == OpenAiAuthMode.ChatGpt
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
                AuthModeSettingName => AuthMode.ToStorageValue(),
                ApiKeySecretName => ApiKey,
                SelectedModelSettingName => SelectedModelId,
                SelectedLlmModelSettingName => SelectedLlmModelId,
                ReasoningEffortSettingName => ReasoningEffort,
                TemperatureModeSettingName => TemperatureMode,
                TemperatureValueSettingName => TemperatureValue.ToString(
                    CultureInfo.InvariantCulture),
                SelectedVoiceSettingName => _selectedVoiceId,
                TtsInstructionsSettingName => TtsInstructions,
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
        AuthMode == OpenAiAuthMode.ChatGpt
            ? await ValidateChatGptAsync(ct)
            : await ValidateApiKeyModeAsync(ct);

    private async Task<PluginSettingsValidationResult?> ValidateApiKeyModeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
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
            await ClearChatGptLoginAsync(ct);
            _forgetChatGptLogin = false;
            return new PluginSettingsValidationResult(true, Loc.L("Settings.ChatGptLoginRemoved"));
        }

        if (HasChatGptCredentials)
        {
            // Stored credentials might have been revoked or expired beyond refresh.
            // ValidOAuthCredentialsAsync returns the cached credentials if the access token is
            // still valid, otherwise hits the refresh endpoint — either way, a
            // failure means the credentials no longer work.
            try
            {
                _ = await ValidOAuthCredentialsAsync(ct);
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
            // The process supervisor launches the browser via xdg-open. On headless or
            // minimal installs with no default browser, that handoff fails here instead
            // of faulting the settings-validation command.
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
        string.IsNullOrWhiteSpace(ChatGptPlanType)
            ? Loc.L("Settings.ChatGptLoginConnected")
            : Loc.L("Settings.ChatGptLoginConnectedPlan", ChatGptPlanType);

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private sealed record OAuthCredentialSnapshot(
        string? AccessToken,
        string? RefreshToken,
        string? IdToken,
        string? AccountId,
        string? PlanType,
        DateTimeOffset? ExpiresAt)
    {
        public static OAuthCredentialSnapshot Empty { get; } =
            new(null, null, null, null, null, null);
    }

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
