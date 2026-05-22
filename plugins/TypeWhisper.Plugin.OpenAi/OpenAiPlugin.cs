using System.ComponentModel;
using System.Diagnostics;
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
        IPluginSettingsProvider
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
    public string PluginVersion => "1.1.0";

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
            ? _selectedLlmModelId ?? SupportedModels.First().Id
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
            throw new InvalidOperationException("API key not configured");

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
            ct
        );
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
            throw new InvalidOperationException("API key not configured");

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

    internal async Task<IReadOnlyList<PluginModelInfo>> RefreshAvailableLlmModelsAsync(
        CancellationToken ct = default)
    {
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
            modelId = SupportedModels.FirstOrDefault()?.Id ?? modelId;

        _selectedLlmModelId = modelId;
        _host?.SetSetting(SelectedLlmModelSettingName, modelId);
    }

    internal void SetReasoningEffort(string effort)
    {
        _reasoningEffort = NormalizeReasoningEffort(effort);
        _host?.SetSetting(ReasoningEffortSettingName, _reasoningEffort);
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
        authFilePath ??= Path.Combine(
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
    internal IPluginLocalization? Loc => _host?.Localization;

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
            throw new InvalidOperationException("ChatGPT login is not configured.");

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

    private void NormalizeSelectedLlmModel(bool persist)
    {
        var available = SupportedModels;
        if (available.Count == 0)
            return;

        if (_selectedLlmModelId is null
            || available.All(model => !string.Equals(model.Id, _selectedLlmModelId, StringComparison.Ordinal)))
        {
            _selectedLlmModelId = available.First().Id;
            if (persist)
                _host?.SetSetting(SelectedLlmModelSettingName, _selectedLlmModelId);
        }
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
                Label: "Connection method",
                Description: "Choose how prompt processing connects to OpenAI. "
                    + "API key uses the OpenAI API; ChatGPT login reuses an existing ChatGPT subscription.",
                Options:
                [
                    new PluginSettingOption(OpenAiAuthMode.ApiKey.ToStorageValue(), "API key"),
                    new PluginSettingOption(OpenAiAuthMode.ChatGpt.ToStorageValue(), "ChatGPT login"),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: ApiKeySecretName,
                Label: "API key",
                IsSecret: true,
                Placeholder: "sk-...",
                Description: "Stored securely and used for OpenAI transcription, LLM, and TTS requests. "
                    + "Required for transcription and text-to-speech even in ChatGPT login mode."
            ),
            new(
                Key: SelectedModelSettingName,
                Label: "Transcription model",
                Description: "Choose the OpenAI speech-to-text model.",
                Options: TranscriptionModelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: "LLM model",
                Description: _authMode == OpenAiAuthMode.ChatGpt
                    ? "ChatGPT login mode uses the supported ChatGPT model list for prompt processing."
                    : _fetchedLlmModels.Count > 0
                        ? $"Showing {_fetchedLlmModels.Count} OpenAI LLM model(s) fetched from the API."
                        : "Using the default OpenAI model list. Click Validate to test the key and fetch current models.",
                Options: SupportedModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: ReasoningEffortSettingName,
                Label: "Reasoning effort",
                Description: "Reasoning effort for GPT-5, o-series, and Codex models.",
                Options:
                [
                    new PluginSettingOption("low", "Low"),
                    new PluginSettingOption("medium", "Medium"),
                    new PluginSettingOption("high", "High"),
                    new PluginSettingOption("xhigh", "X High"),
                ],
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: SelectedVoiceSettingName,
                Label: "Text-to-speech voice",
                Description: "Choose the OpenAI text-to-speech voice.",
                Options: AvailableVoices
                    .Select(v => new PluginSettingOption(v.Id, v.DisplayName))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
            new(
                Key: TtsInstructionsSettingName,
                Label: "Voice instructions",
                Placeholder: "Optional",
                Description: "Optional. Style guidance applied to the text-to-speech voice.",
                Kind: PluginSettingKind.Multiline
            ),
            new(
                Key: ForgetChatGptLoginSettingName,
                Label: "Forget ChatGPT login",
                Description: "When set, the next Validate removes the stored ChatGPT login.",
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
                SelectedVoiceSettingName => _selectedVoiceId,
                TtsInstructionsSettingName => _ttsInstructions,
                ForgetChatGptLoginSettingName => _forgetChatGptLogin ? "true" : "false",
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
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default) =>
        _authMode == OpenAiAuthMode.ChatGpt
            ? await ValidateChatGptAsync(ct)
            : await ValidateApiKeyModeAsync(ct);

    private async Task<PluginSettingsValidationResult?> ValidateApiKeyModeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, "Enter an API key first.");

        var valid = await ValidateApiKeyAsync(_apiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, "API key is invalid.");

        var models = await RefreshAvailableLlmModelsAsync(ct);
        return new PluginSettingsValidationResult(
            true,
            models.Count > 0
                ? $"API key is valid. Fetched {models.Count} OpenAI LLM model(s)."
                : "API key is valid. Using saved/default models."
        );
    }

    private async Task<PluginSettingsValidationResult?> ValidateChatGptAsync(CancellationToken ct)
    {
        if (_forgetChatGptLogin)
        {
            await ClearChatGptLoginAsync();
            _forgetChatGptLogin = false;
            return new PluginSettingsValidationResult(true, "ChatGPT login removed.");
        }

        if (HasChatGptCredentials)
            return new PluginSettingsValidationResult(true, ChatGptConnectedMessage());

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
                false, $"Could not import existing ChatGPT login: {ex.Message}");
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
                "Could not start the login listener on port 1455. "
                    + "Close any application using that port and try again.");
        }
        catch (Win32Exception ex)
        {
            // Process.Start(UseShellExecute=true) launches the browser via
            // xdg-open on Linux. On headless boxes or minimal installs with no
            // default browser registered, that call throws Win32Exception and
            // would otherwise fault the settings-validation command.
            return new PluginSettingsValidationResult(
                false,
                "Could not open a browser for ChatGPT login. "
                    + "Install xdg-utils or set a default browser and try again."
                    + $" ({ex.Message})");
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            return new PluginSettingsValidationResult(false, $"ChatGPT login failed: {ex.Message}");
        }
    }

    private string ChatGptConnectedMessage() =>
        string.IsNullOrWhiteSpace(_oauthPlanType)
            ? "ChatGPT login connected."
            : $"ChatGPT login connected. Plan: {_oauthPlanType}.";

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private sealed record TranscriptionModelEntry(
        string Id,
        string DisplayName,
        string ApiModelName,
        string ResponseFormat,
        bool SupportsTranslation
    );

    private sealed record OpenAiModelsResponse(List<OpenAiFetchedModel> Data);
}
