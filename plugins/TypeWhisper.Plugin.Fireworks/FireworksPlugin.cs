// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Fireworks;

public sealed class FireworksPlugin
    : ILlmProviderPlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    // OpenAI-compatible routes live under /inference; /v1 alone is the control plane and returns 404.
    private const string BaseUrl = "https://api.fireworks.ai/inference";

    private const string FetchedModelsSettingKey = "fetchedModels";
    private const string TemperatureModeSettingKey = "temperatureMode";
    private const string TemperatureSettingKey = "temperature";
    private const string ProviderDefaultTemperatureMode = "provider-default";
    private const string CustomTemperatureMode = "custom";

    // The shared chat helper's fixed temperature, sent before it became a setting.
    private const double DefaultTemperature = 0.1;

    private const int ModelPageSize = 200;
    private const int MaxModelPages = 10;
    private const int MaxCatalogModels = 1000;
    private const int MaxModelIdLength = 256;

    internal const string DefaultModelId = "accounts/fireworks/models/gpt-oss-120b";

    // Fireworks serverless models as of 2026-10-17; GPT-OSS 120B is upstream typewhisper-win's verified default.
    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackModels =
    [
        new(DefaultModelId, "GPT-OSS 120B") { IsRecommended = true },
        new("accounts/fireworks/models/glm-5p3-flash", "GLM 5.3 Flash"),
    ];

    private static readonly Dictionary<string, string> s_knownDisplayNames = new(StringComparer.Ordinal)
    {
        [DefaultModelId] = "GPT-OSS 120B",
        ["accounts/fireworks/models/gpt-oss-20b"] = "GPT-OSS 20B",
        ["accounts/fireworks/models/glm-5p3-flash"] = "GLM 5.3 Flash",
        ["accounts/fireworks/models/glm-5p3"] = "GLM 5.3",
        ["accounts/fireworks/models/kimi-k3"] = "Kimi K3",
        ["accounts/fireworks/models/deepseek-v4p1-flash"] = "DeepSeek V4.1 Flash",
        ["accounts/fireworks/models/minimax-m3"] = "MiniMax M3",
        ["accounts/fireworks/models/qwen3p8-max"] = "Qwen 3.8 Max",
    };

    // Reason by default (tokens count against max_tokens); matched by name to cover account fine-tunes.
    private static readonly string[] s_reasoningFamilies = ["gpt-oss-", "glm-5"];

    // Listed without a chat capability flag, these names are never chat models.
    private static readonly string[] s_nonChatMarkers = ["whisper", "embedding", "reranker", "image", "stable-diffusion", "flux"];

    private readonly HttpClient _httpClient;
    private readonly Lock _catalogLock = new();
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private bool _streamResponses = true;
    private string _temperatureMode = CustomTemperatureMode;
    private double _temperature = DefaultTemperature;

    // Bumped on key change or deactivation so an in-flight refresh cannot commit.
    private long _catalogGeneration;

    public FireworksPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    internal FireworksPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.fireworks";
    public string PluginName => "Fireworks";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("apiKey");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingKey));
        _temperature = host.GetSetting<double?>(TemperatureSettingKey) is { } saved && IsTemperature(saved)
            ? saved
            : DefaultTemperature;
        lock (_catalogLock)
            SetCatalog(LoadSavedCatalog(host, _apiKey));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        lock (_catalogLock)
        {
            _catalogGeneration++;
            _host = null;
        }

        return Task.CompletedTask;
    }

    public string ProviderName => "Fireworks";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; private set; } = s_fallbackModels;

    // Discovery is bounded as a whole, not only by the HTTP client timeout.
    internal TimeSpan ModelDiscoveryDeadline { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveModelId(model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateRequestOptions(modelId, systemPrompt, userText),
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

        var modelId = ResolveModelId(model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            _apiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateRequestOptions(modelId, systemPrompt, userText),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    private string ResolveModelId(string? model) =>
        string.IsNullOrWhiteSpace(model) ? SupportedModels[0].Id : model.Trim();

    private OpenAiChatRequestOptions CreateRequestOptions(string modelId, string systemPrompt, string userText) =>
        new()
        {
            ProviderName = ProviderName,
            MaxOutputTokens = IsReasoningModel(modelId)
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
            Temperature = _temperatureMode == CustomTemperatureMode ? _temperature : null,
        };

    private static bool IsReasoningModel(string modelId) =>
        s_reasoningFamilies.Any(family => ModelName(modelId).StartsWith(family, StringComparison.OrdinalIgnoreCase));

    // The <name> of accounts/<account>/models/<name>, so the account name is never matched.
    private static string ModelName(string modelId) => modelId[(modelId.LastIndexOf('/') + 1)..];

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var trimmed = apiKey.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        IPluginHostServices? host;
        // Serialized so an overlapping save cannot restore a key another save already removed.
        await _apiKeyWriteLock.WaitAsync();
        try
        {
            // The saved catalog is keyed to its owner, so a failed secret write leaves the old pair intact.
            host = _host;
            if (host is not null)
            {
                if (normalized is null)
                    await host.DeleteSecretAsync("apiKey");
                else
                    await host.StoreSecretAsync("apiKey", normalized);
            }

            if (!string.Equals(_apiKey, normalized, StringComparison.Ordinal))
            {
                lock (_catalogLock)
                {
                    _catalogGeneration++;
                    _apiKey = normalized;
                    SetCatalog(host is null ? [] : LoadSavedCatalog(host, normalized));
                }
            }
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        host?.NotifyCapabilitiesChanged();
    }

    // A catalog saved for a different key is ignored.
    private static List<string> LoadSavedCatalog(IPluginHostServices host, string? apiKey) =>
        host.GetSetting<FireworksModelCatalog>(FetchedModelsSettingKey) is { } saved && saved.Owner == Fingerprint(apiKey)
            ? NormalizeModels(saved.Models ?? [])
            : [];

    internal static string Fingerprint(string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"TypeWhisper.Fireworks.catalog/1\n{apiKey}")));

    /// <summary>
    ///     Lists the chat models available to the key across every page. An empty, malformed or
    ///     oversized catalog is rejected whole, so a partial list is never saved.
    /// </summary>
    internal async Task<List<string>> FetchModelsAsync(string apiKey, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDiscoveryDeadline);
        try
        {
            var models = new List<string>();
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var listed = 0;
            string? pageToken = null;
            for (var page = 0; ; page++)
            {
                var url = $"{BaseUrl}/v1/models?page_size={ModelPageSize}"
                    + (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                    _httpClient, request, HttpCompletionOption.ResponseContentRead, deadline.Token);
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                var parsed = ParseModelPage(body) ?? throw InvalidCatalog();

                listed += parsed.Listed;
                if (listed > MaxCatalogModels)
                    throw InvalidCatalog();
                models.AddRange(parsed.ChatModels);

                if (parsed.NextPageToken is null)
                    break;
                if (page + 1 >= MaxModelPages || !seenTokens.Add(parsed.NextPageToken))
                    throw InvalidCatalog();
                pageToken = parsed.NextPageToken;
            }

            var normalized = NormalizeModels(models);
            return normalized.Count == 0 ? throw InvalidCatalog() : normalized;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                Loc.L("Errors.ModelRefreshTimedOut"), PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    private sealed record ModelPage(List<string> ChatModels, int Listed, string? NextPageToken);

    private static ModelPage? ParseModelPage(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() > MaxCatalogModels)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !IsModelId(id.GetString()))
                {
                    return null;
                }

                if (IsChatModel(item, id.GetString()!))
                    ids.Add(id.GetString()!);
            }

            string? next = null;
            foreach (var name in (string[])["next_page_token", "nextPageToken"])
            {
                if (!root.TryGetProperty(name, out var token) || token.ValueKind == JsonValueKind.Null)
                    continue;
                if (token.ValueKind != JsonValueKind.String)
                    return null;
                if (!string.IsNullOrEmpty(token.GetString()))
                    next = token.GetString();
            }

            return new ModelPage(ids, data.GetArrayLength(), next);
        }
    }

    // Fireworks lists embedding, image and audio models alongside chat models.
    private static bool IsChatModel(JsonElement item, string id)
    {
        if (item.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && string.Equals(kind.GetString(), "EMBEDDING_MODEL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.TryGetProperty("supports_chat", out var chat) && chat.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return chat.ValueKind == JsonValueKind.True;

        return !s_nonChatMarkers.Any(marker => ModelName(id).Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private PluginRequestException InvalidCatalog() =>
        new(Loc.L("Errors.ModelCatalogInvalid"),
            PluginRequestFailureKind.OutputIncomplete, isTransient: false);

    // Commits only when no key change or deactivation happened since the refresh began.
    private bool TryCommitCatalog(List<string> models, long generation, string apiKey)
    {
        IPluginHostServices host;
        lock (_catalogLock)
        {
            if (_host is null
                || generation != _catalogGeneration
                || !string.Equals(_apiKey, apiKey, StringComparison.Ordinal))
            {
                return false;
            }

            host = _host;
            // Persist first so a failed write leaves the live catalog untouched.
            host.SetSetting(FetchedModelsSettingKey, new FireworksModelCatalog(Fingerprint(apiKey), models));
            SetCatalog(models);
        }

        host.NotifyCapabilitiesChanged();
        return true;
    }

    // Caller holds _catalogLock. The default leads so blank-model requests keep using it.
    private void SetCatalog(List<string> models)
    {
        SupportedModels = models.Count == 0
            ? s_fallbackModels
            : models
                .OrderBy(id => id == DefaultModelId ? 0 : 1)
                .ThenBy(id => id, StringComparer.Ordinal)
                .Select(id => new PluginModelInfo(id, s_knownDisplayNames.GetValueOrDefault(id, id))
                {
                    IsRecommended = id == DefaultModelId,
                })
                .ToList();
    }

    private static List<string> NormalizeModels(IEnumerable<string?> models) =>
        models
            .Where(IsModelId)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool IsModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxModelIdLength
        && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "apiKey",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "fw-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: TemperatureModeSettingKey,
                Label: Loc.L("Settings.TemperatureMode"),
                Description: Loc.L("Settings.TemperatureModeDescription"),
                Kind: PluginSettingKind.Dropdown,
                Options:
                [
                    new PluginSettingOption(ProviderDefaultTemperatureMode, Loc.L("Settings.TemperatureProviderDefault")),
                    new PluginSettingOption(CustomTemperatureMode, Loc.L("Settings.TemperatureCustom")),
                ]
            ),
            new(
                Key: TemperatureSettingKey,
                Label: Loc.L("Settings.TemperatureValue"),
                Description: Loc.L("Settings.TemperatureValueDescription"),
                Placeholder: DefaultTemperature.ToString(CultureInfo.InvariantCulture),
                Kind: PluginSettingKind.Text
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "apiKey" => _apiKey,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                TemperatureModeSettingKey => _temperatureMode,
                TemperatureSettingKey => _temperature.ToString(CultureInfo.InvariantCulture),
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
            case TemperatureModeSettingKey:
                var mode = NormalizeTemperatureMode(value);
                _host?.SetSetting(TemperatureModeSettingKey, mode);
                _temperatureMode = mode;
                break;
            case TemperatureSettingKey:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                    || !IsTemperature(temperature))
                {
                    throw new ArgumentException(Loc.L("Settings.TemperatureInvalid"));
                }

                _host?.SetSetting(TemperatureSettingKey, temperature);
                _temperature = temperature;
                break;
        }
    }

    // Anything but an explicit provider default keeps the previous fixed temperature.
    private static string NormalizeTemperatureMode(string? value) =>
        value == ProviderDefaultTemperatureMode ? ProviderDefaultTemperatureMode : CustomTemperatureMode;

    private static bool IsTemperature(double value) => double.IsFinite(value) && value is >= 0 and <= 2;

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Validation doubles as the model refresh; any failure leaves the saved catalog untouched.
    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var apiKey = _apiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        long generation;
        lock (_catalogLock)
            generation = _catalogGeneration;

        List<string> models;
        try
        {
            models = await FetchModelsAsync(apiKey, ct);
        }
        catch (PluginRequestException ex) when (ex.FailureKind is PluginRequestFailureKind.Authentication
                                                    or PluginRequestFailureKind.Permission)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyRejected"));
        }
        catch (PluginRequestException ex)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshFailed", ex.Message));
        }

        return TryCommitCatalog(models, generation, apiKey)
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValidFetchedModels", models.Count))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshStale"));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}

internal sealed record FireworksModelCatalog(string? Owner, List<string>? Models);
