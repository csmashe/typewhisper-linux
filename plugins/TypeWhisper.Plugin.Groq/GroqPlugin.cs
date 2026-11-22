// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Groq;

public sealed class GroqPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.groq.com/openai";
    private const string FetchedLlmModelsSetting = "fetchedLlmModels";
    private const string FetchedLlmModelsAtSetting = "fetchedLlmModelsAtUtc";

    // Groq's named replacement for the retired Llama models.
    internal const string DefaultLlmModelId = "openai/gpt-oss-120b";

    internal static readonly TimeSpan s_llmModelRefreshInterval = TimeSpan.FromHours(24);

    internal static readonly TimeSpan s_llmModelRefreshRetryDelay = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly bool _refreshLlmModelsAutomatically;
    private readonly Lock _llmModelsLock = new();
    private IPluginHostServices? _host;
    private string? _selectedApiModelName;
    private string? _preferredLlmModelId;
    private List<FetchedLlmModel> _fetchedLlmModels = [];
    private DateTimeOffset? _lastLlmModelRefreshAttemptAt;
    private CancellationTokenSource? _llmModelRefreshCts;
    private bool _streamResponses = true;

    // Whisper language tokens (openai/whisper tokenizer.py LANGUAGES); both Groq models are Whisper Large V3 builds.
    private static readonly ReadOnlyCollection<string> s_transcriptionLanguages = Array.AsReadOnly(
        "en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue".Split(' ')
    );

    private static readonly IReadOnlyList<TranscriptionModelEntry> s_transcriptionModelEntries =
    [
        new("whisper-large-v3", "Whisper Large V3", "whisper-large-v3", SupportsTranslation: true),
        new(
            "whisper-large-v3-turbo",
            "Whisper Large V3 Turbo",
            "whisper-large-v3-turbo",
            SupportsTranslation: false
        ),
    ];

    // Groq retired Llama 3.3 70B, Llama 3.1 8B and Kimi K2 for free and developer accounts.
    // The default comes first: the host takes SupportedModels[0] when no model is chosen.
    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new(DefaultLlmModelId, "GPT-OSS 120B"),
        new("openai/gpt-oss-20b", "GPT-OSS 20B"),
        new("qwen/qwen3.8-27b", "Qwen 3.8 27B"),
    ];

    public GroqPlugin()
        : this(CreateHttpClient(), refreshLlmModelsAutomatically: true) { }

    internal GroqPlugin(
        HttpClient httpClient,
        bool refreshLlmModelsAutomatically = false,
        TimeProvider? clock = null
    )
    {
        _httpClient = httpClient;
        _refreshLlmModelsAutomatically = refreshLlmModelsAutomatically;
        _clock = clock ?? TimeProvider.System;
    }

    public string PluginId => "com.typewhisper.groq";
    public string PluginName => "Groq";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        SelectedModelId =
            host.GetSetting<string>("selectedModel") ?? s_transcriptionModelEntries[0].Id;
        _preferredLlmModelId = host.GetSetting<string>("selectedLlmModel");
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<FetchedLlmModel>>(FetchedLlmModelsSetting) ?? []
        );
        // A list saved before fetch dates were stored counts as stale and is refreshed below.
        FetchedLlmModelsAt = LoadFetchedLlmModelsAt(host);
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;

        var selectedTranscription =
            s_transcriptionModelEntries.FirstOrDefault(m => m.Id == SelectedModelId)
            ?? s_transcriptionModelEntries[0];
        SelectedModelId = selectedTranscription.Id;
        _selectedApiModelName = selectedTranscription.ApiModelName;

        NormalizeSelectedLlmModel();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
        StartLlmModelRefreshIfStale(replaceInFlight: true);
    }

    public Task DeactivateAsync()
    {
        CancelLlmModelRefresh();
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "groq";
    public string ProviderDisplayName => "Groq";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        s_transcriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)
        {
            LanguageCount = s_transcriptionLanguages.Count,
        }).ToList();

    public IReadOnlyList<string> SupportedLanguages => s_transcriptionLanguages;

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation
    {
        get
        {
            if (!IsConfigured || SelectedModelId is null)
                return false;
            var entry = s_transcriptionModelEntries.FirstOrDefault(m => m.Id == SelectedModelId);
            return entry?.SupportsTranslation ?? false;
        }
    }

    private string SelectedModelDisplayName =>
        s_transcriptionModelEntries.FirstOrDefault(m => m.Id == SelectedModelId)?.DisplayName
        ?? SelectedModelId
        ?? "";

    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public void SelectModel(string modelId)
    {
        var entry =
            s_transcriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = modelId;
        _selectedApiModelName = entry.ApiModelName;
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
        if (!IsConfigured || _selectedApiModelName is null)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        if (translate && !SupportsTranslation)
            throw new PluginRequestException(
                Loc.L("Settings.TranslationUnsupported", SelectedModelDisplayName), PluginRequestFailureKind.InvalidRequest
            );

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            _selectedApiModelName,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt
        );
    }

    public string ProviderName => "Groq";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : s_fallbackLlmModels;

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        StartLlmModelRefreshIfStale();
        var modelId = ResolveLlmModelId(string.IsNullOrWhiteSpace(model) ? null : model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            BuildRequestOptions(modelId),
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

        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        StartLlmModelRefreshIfStale();
        var modelId = ResolveLlmModelId(string.IsNullOrWhiteSpace(model) ? null : model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            BuildRequestOptions(modelId),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    private static OpenAiChatRequestOptions BuildRequestOptions(string modelId)
    {
        // Groq returns Qwen reasoning inline unless the reasoning format is hidden.
        return modelId.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiChatRequestOptions
            {
                ProviderName = "Groq",
                AdditionalBodyFields = new Dictionary<string, object?> { ["reasoning_format"] = "hidden" },
            }
            : new OpenAiChatRequestOptions { ProviderName = "Groq" };
    }

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    internal string? SelectedLlmModelId { get; private set; }

    internal IReadOnlyList<FetchedLlmModel> FetchedLlmModels => _fetchedLlmModels;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(ApiKey, normalizedApiKey, StringComparison.Ordinal);

        ApiKey = normalizedApiKey;
        if (_host is not null)
        {
            if (normalizedApiKey is null)
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", normalizedApiKey);

            if (changed)
            {
                // A list fetched with another key can offer models this account cannot use.
                CancelLlmModelRefresh();
                lock (_llmModelsLock)
                {
                    _fetchedLlmModels = [];
                    FetchedLlmModelsAt = null;
                    _preferredLlmModelId = null;
                    _host.SetSetting(FetchedLlmModelsSetting, _fetchedLlmModels);
                    _host.SetSetting(FetchedLlmModelsAtSetting, FetchedLlmModelsAt);
                    _host.SetSetting("selectedLlmModel", _preferredLlmModelId);
                    NormalizeSelectedLlmModel();
                }

                if (wasConfigured != IsConfigured)
                    _host.NotifyCapabilitiesChanged();
                StartLlmModelRefreshIfStale(replaceInFlight: true);
            }
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        lock (_llmModelsLock)
        {
            _preferredLlmModelId = modelId;
            SelectedLlmModelId = modelId;
            _host?.SetSetting("selectedLlmModel", modelId);
        }
    }

    internal DateTimeOffset? FetchedLlmModelsAt { get; private set; }
    internal Task LlmModelRefreshTask { get; private set; } = Task.CompletedTask;

    internal void SetFetchedLlmModels(List<FetchedLlmModel> models)
    {
        lock (_llmModelsLock)
            ApplyFetchedLlmModels(models);

        _host?.NotifyCapabilitiesChanged();
    }

    // Callers hold _llmModelsLock.
    private void ApplyFetchedLlmModels(List<FetchedLlmModel> models)
    {
        _fetchedLlmModels = NormalizeFetchedLlmModels(models);
        FetchedLlmModelsAt = _clock.GetUtcNow();
        _host?.SetSetting(FetchedLlmModelsSetting, _fetchedLlmModels);
        _host?.SetSetting(FetchedLlmModelsAtSetting, FetchedLlmModelsAt);
        NormalizeSelectedLlmModel();
    }

    internal bool IsLlmModelListStale(DateTimeOffset now) =>
        FetchedLlmModelsAt is not { } fetchedAt
        || fetchedAt > now
        || now - fetchedAt >= s_llmModelRefreshInterval;

    // Groq retires models at short notice; a daily reload drops them instead of every request
    // failing with HTTP 404. Requests start a refresh only when none runs and the retry delay passed.
    private void StartLlmModelRefreshIfStale(bool replaceInFlight = false)
    {
        lock (_llmModelsLock)
        {
            var now = _clock.GetUtcNow();
            if (!_refreshLlmModelsAutomatically || _host is null || ApiKey is not { } apiKey
                || !IsLlmModelListStale(now))
                return;

            if (!replaceInFlight
                && (!LlmModelRefreshTask.IsCompleted
                    || _lastLlmModelRefreshAttemptAt is { } lastAttempt
                    && lastAttempt <= now
                    && now - lastAttempt < s_llmModelRefreshRetryDelay))
                return;

            CancelLlmModelRefresh();
            var cts = new CancellationTokenSource();
            // Read the token before publishing the source: a cancellation disposes it.
            var token = cts.Token;
            _llmModelRefreshCts = cts;
            _lastLlmModelRefreshAttemptAt = now;
            LlmModelRefreshTask = Task.Run(() => RefreshLlmModelsInBackgroundAsync(apiKey, token), token);
        }
    }

    private async Task RefreshLlmModelsInBackgroundAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            var models = NormalizeFetchedLlmModels(await FetchLlmModelsAsync(apiKey, ct) ?? []);
            if (models.Count == 0)
            {
                _host?.Log(PluginLogLevel.Warning, "Groq returned no usable model list. Keeping the current list.");
                return;
            }

            IPluginHostServices? host;
            lock (_llmModelsLock)
            {
                // Drop a result that arrives after deactivation or a key change.
                host = _host;
                if (ct.IsCancellationRequested || host is null
                    || !string.Equals(ApiKey, apiKey, StringComparison.Ordinal))
                    return;

                ApplyFetchedLlmModels(models);
            }

            host.NotifyCapabilitiesChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Groq model refresh failed: {ex.GetType().Name}");
        }
    }

    private void CancelLlmModelRefresh()
    {
        var cts = Interlocked.Exchange(ref _llmModelRefreshCts, null);
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    internal Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(CancellationToken ct = default) =>
        FetchLlmModelsAsync(ApiKey, ct);

    private async Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedLlmModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null
                ))
                .Where(m => IsLlmModel(m.Id))
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller explicitly cancelled — propagate so ValidateAsync surfaces
            // cancellation instead of a misleading "couldn't fetch models" result.
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeout (its 30s budget fires an OCE on an internal
            // token, not ct). Treat as transient: fall back to saved/default
            // models so a slow network doesn't poison ValidateAsync.
            return null;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    internal static bool IsLlmModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var excluded = new[]
        {
            "whisper",
            "distil-whisper",
            "tool-use",
            "orpheus",
            "tts",
            "prompt-guard",
            "safeguard",
        };

        return !excluded.Any(lowered.Contains);
    }

    internal string ResolveLlmModelId(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : SelectedLlmModelId ?? SupportedModels[0].Id;

    // A saved model the list no longer offers falls back to the default, but stays saved
    // so a later list that offers it applies it again.
    private void NormalizeSelectedLlmModel()
    {
        var models = SupportedModels;
        SelectedLlmModelId = models
            .FirstOrDefault(m => string.Equals(m.Id, _preferredLlmModelId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? (models.Count > 0 ? models[0].Id : null);
    }

    // Default first: the host takes SupportedModels[0] when no model is chosen.
    private static List<FetchedLlmModel> NormalizeFetchedLlmModels(
        IEnumerable<FetchedLlmModel> models
    ) =>
        models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && IsLlmModel(m.Id))
            .DistinctBy(m => m.Id)
            .OrderBy(m => !string.Equals(m.Id, DefaultLlmModelId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DateTimeOffset? LoadFetchedLlmModelsAt(IPluginHostServices host)
    {
        try
        {
            var value = host.GetSetting<DateTimeOffset?>(FetchedLlmModelsAtSetting);
            return value == default(DateTimeOffset) ? null : value?.ToUniversalTime();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        CancelLlmModelRefresh();
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "api-key",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "gsk_...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: "selectedModel",
                Label: Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.TranscriptionModelDescription"),
                Options: s_transcriptionModelEntries
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
            new(
                Key: "selectedLlmModel",
                Label: Loc.L("Settings.LlmModel"),
                Description: _fetchedLlmModels.Count > 0
                    ? Loc.L("Settings.LlmModelFetched", _fetchedLlmModels.Count)
                    : Loc.L("Settings.LlmModelDefault"),
                Options: SupportedModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
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
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
                "selectedLlmModel" => SelectedLlmModelId,
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
            case "api-key":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case "selectedLlmModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectLlmModel(value);
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
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        CancelLlmModelRefresh();
        var models = await FetchLlmModelsAsync(ct);
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (models is not null)
        {
            SetFetchedLlmModels(models);
            return new PluginSettingsValidationResult(
                true,
                Loc.L("Settings.ApiKeyValidFetchedModels", models.Count)
            );
        }

        return new PluginSettingsValidationResult(
            true,
            Loc.L("Settings.ApiKeyValidSavedModels")
        );
    }

    private sealed record TranscriptionModelEntry(
        string Id,
        string DisplayName,
        string ApiModelName,
        bool SupportsTranslation
    );
}

internal sealed record FetchedLlmModel(string Id, string? OwnedBy);
