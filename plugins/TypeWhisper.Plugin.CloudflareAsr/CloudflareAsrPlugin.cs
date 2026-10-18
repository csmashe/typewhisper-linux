// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed class CloudflareAsrPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string WhisperModelId = "whisper";
    private const string TurboModelId = "whisper-large-v3-turbo";

    // Cloudflare's Free/Pro request-body ceiling. Turbo sends the WAV as base64 JSON, a third larger.
    private const int MaxWhisperUploadBytes = 100_000_000;
    private const int MaxTurboUploadBytes = 74_000_000;

    private static readonly TimeSpan s_validationTimeout = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyList<string> s_turboLanguages =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi",
        "fi", "vi", "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
        "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy",
        "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
        "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl",
        "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su",
    ];

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new(WhisperModelId, "Whisper (Cloudflare)"),
        new(TurboModelId, "Whisper Large V3 Turbo (Cloudflare)")
        {
            IsRecommended = true,
            LanguageCount = s_turboLanguages.Count,
        },
    ];

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiToken;
    private string? _accountId;

    public CloudflareAsrPlugin()
        : this(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(120),
            }
        )
    {
    }

    internal CloudflareAsrPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.cloudflare-asr";
    public string PluginName => "Cloudflare ASR";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        // Normalize on load: legacy values saved before SetApiTokenAsync trimmed
        // could otherwise reach the Bearer header with trailing whitespace and
        // 401 every request while IsConfigured still reports true.
        var loadedToken = await host.LoadSecretAsync("api-token");
        _apiToken = string.IsNullOrWhiteSpace(loadedToken) ? null : loadedToken.Trim();
        var loadedAccount = await host.LoadSecretAsync("account-id");
        _accountId = string.IsNullOrWhiteSpace(loadedAccount) ? null : loadedAccount.Trim();
        var savedModel = host.GetSetting<string>("selectedModel");
        SelectedModelId = s_models.Any(m => m.Id == savedModel) ? savedModel : s_models[0].Id;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "cloudflare-asr";
    public string ProviderDisplayName => "Cloudflare ASR";
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_apiToken) && !string.IsNullOrEmpty(_accountId);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; }

    private bool IsTurbo => SelectedModelId == TurboModelId;

    public bool SupportsTranslation => false;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport =>
        IsTurbo ? LanguageSelectionSupport.Supported : LanguageSelectionSupport.Unsupported;
    public IReadOnlyList<string> SupportedLanguages => IsTurbo ? s_turboLanguages : [];

    // Only Turbo takes an initial prompt; the original Whisper endpoint accepts audio alone.
    public bool SupportsStructuredDictionaryTerms => IsTurbo;

    public void SelectModel(string modelId)
    {
        if (s_models.All(m => m.Id != modelId))
            throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
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
            throw new NotSupportedException(
                "Translation is not supported by the Cloudflare ASR plugin."
            );

        var modelId = SelectedModelId ?? s_models[0].Id;
        var isTurbo = modelId == TurboModelId;

        // "auto" is the sentinel for "no explicit language".
        var normalized = language?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
            normalized = null;

        // Defense in depth for direct/legacy callers; the typed host invoker resolves the
        // selection against the advertised capabilities before entering the plugin.
        if (normalized is not null && !isTurbo)
            throw new NotSupportedException(
                "This Cloudflare model does not support explicit language selection. Choose Whisper Large V3 Turbo or use automatic detection."
            );
        if (normalized is not null && !s_turboLanguages.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Cloudflare Whisper Large V3 Turbo does not support the language '{normalized}'."
            );

        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.EnterAccountIdAndApiToken"), PluginRequestFailureKind.Configuration);

        var accountId = _accountId!;
        if (!IsValidAccountId(accountId))
            throw new PluginRequestException(Loc.L("Settings.InvalidAccountId"), PluginRequestFailureKind.Configuration);

        if (wavAudio.Length > (isTurbo ? MaxTurboUploadBytes : MaxWhisperUploadBytes))
            throw new PluginRequestException(Loc.L("Errors.RecordingTooLarge"), PluginRequestFailureKind.RequestTooLarge);

        var url = $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/@cf/openai/{modelId}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        if (isTurbo)
        {
            request.Content = new TurboAudioContent(
                wavAudio,
                normalized?.ToLowerInvariant(),
                PluginTranscriptionPrompt.Parse(prompt).ToPlainText()
            );
        }
        else
        {
            request.Content = new ByteArrayContent(wavAudio);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            // Status + reason only: the body can echo request fragments or token-bearing identifiers.
            (errorResponse, _) =>
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Cloudflare API error {(int)errorResponse.StatusCode} ({errorResponse.ReasonPhrase})"
                );
                return $"Cloudflare API error {(int)errorResponse.StatusCode}: {errorResponse.ReasonPhrase}";
            });
        var json = await response.Content.ReadAsStringAsync(ct);

        return ParseTranscription(json);
    }

    private static PluginTranscriptionResult ParseTranscription(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw InvalidResponse("the body is not valid JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
            )
                throw InvalidResponse("'success' must be true");

            if (
                !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("text", out var textEl)
                || textEl.ValueKind != JsonValueKind.String
            )
                throw InvalidResponse("required field 'result.text' must be a string");

            // Turbo nests language and duration under result.transcription_info; both are
            // optional and absent when Cloudflare can't determine them.
            var info =
                result.TryGetProperty("transcription_info", out var details)
                && details.ValueKind == JsonValueKind.Object
                    ? details
                    : result;

            string? detectedLanguage = null;
            if (info.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String)
                detectedLanguage = langEl.GetString();

            double duration = 0;
            if (
                info.TryGetProperty("duration", out var durEl)
                && durEl.ValueKind == JsonValueKind.Number
                && durEl.TryGetDouble(out var parsedDuration)
                && double.IsFinite(parsedDuration)
                && parsedDuration >= 0
            )
                duration = parsedDuration;

            return new PluginTranscriptionResult(
                (textEl.GetString() ?? "").Trim(),
                detectedLanguage,
                duration,
                NoSpeechProbability: null
            );
        }
    }

    private static PluginRequestException InvalidResponse(string reason, Exception? inner = null) =>
        new(
            $"Invalid Cloudflare transcription response: {reason}.",
            PluginRequestFailureKind.OutputIncomplete,
            innerException: inner
        );

    // Cloudflare account IDs are 32 hex characters; anything else would also alter the request path.
    internal static bool IsValidAccountId(string? accountId) =>
        accountId is { Length: 32 } && accountId.All(char.IsAsciiHexDigit);

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetAccountIdAsync(string accountId)
    {
        _accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                await _host.DeleteSecretAsync("account-id");
            else
                await _host.StoreSecretAsync("account-id", accountId.Trim());

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task SetApiTokenAsync(string apiToken)
    {
        var trimmed = apiToken.Trim();
        _apiToken = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-token");
            else
                await _host.StoreSecretAsync("api-token", trimmed);

            _host.NotifyCapabilitiesChanged();
        }
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "account-id",
                Loc.L("Settings.AccountId"),
                false,
                null,
                Loc.L("Settings.AccountIdDescription")
            ),
            new(
                "api-token",
                Loc.L("Settings.ApiToken"),
                true,
                null,
                Loc.L("Settings.ApiTokenDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "account-id" => _accountId,
                "api-token" => _apiToken,
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
            case "account-id":
                await SetAccountIdAsync(value ?? string.Empty);
                break;
            case "api-token":
                await SetApiTokenAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var accountId = _accountId;
        var apiToken = _apiToken;
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(apiToken))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterAccountIdAndApiToken"));

        if (!IsValidAccountId(accountId))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.InvalidAccountId"));

        var problem = await ProbeWorkersAiAccessAsync(accountId, apiToken, ct);
        if (accountId != _accountId || apiToken != _apiToken)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.SettingsChangedDuringValidation"));

        return problem is null
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.CredentialsValid"))
            : new PluginSettingsValidationResult(false, problem);
    }

    // Proves Workers AI access for this account without uploading audio. Null on success, else the problem.
    private async Task<string?> ProbeWorkersAiAccessAsync(string accountId, string apiToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/models/search?per_page=1"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_validationTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Loc.L("Settings.TokenRejected");
            if (!response.IsSuccessStatusCode)
                return Loc.L("Settings.ValidationFailedStatus", (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            return IsSuccessfulModelList(body) ? null : Loc.L("Settings.InvalidValidationResponse");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Loc.L("Settings.CloudflareUnreachable");
        }
        // A body cut off mid-read surfaces as an IOException rather than HttpRequestException.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Loc.L("Settings.CloudflareUnreachable");
        }
    }

    private static bool IsSuccessfulModelList(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
