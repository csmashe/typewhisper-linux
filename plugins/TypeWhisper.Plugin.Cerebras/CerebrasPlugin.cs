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

namespace TypeWhisper.Plugin.Cerebras;

public sealed class CerebrasPlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.cerebras.ai";

    private const string FetchedModelsSettingKey = "fetchedModels";
    private const string TemperatureModeSettingKey = "temperatureMode";
    private const string TemperatureSettingKey = "temperature";
    private const string ProviderDefaultTemperatureMode = "provider-default";
    private const string CustomTemperatureMode = "custom";

    // Every request sent this temperature before it became a setting.
    private const double DefaultTemperature = 0.1;

    private const int MaxCatalogModels = 1000;
    private const int MaxModelIdLength = 256;

    internal const string DefaultModelId = "gpt-oss-120b";

    // Cerebras's shared-inference catalog as of 2026-10-16, in upstream typewhisper-win order.
    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackModels =
    [
        new(DefaultModelId, "GPT-OSS 120B"),
        new("qwen-3.8-27b", "Qwen 3.8 27B"),
    ];

    private static readonly Dictionary<string, string> s_knownDisplayNames = new(StringComparer.Ordinal)
    {
        [DefaultModelId] = "GPT-OSS 120B",
        ["qwen-3.8-27b"] = "Qwen 3.8 27B",
        ["gemma-4-31b"] = "Gemma 4 31B",
        ["llama3.1-8b"] = "Llama 3.1 8B",
        ["qwen-3-235b-a22b-instruct-2507"] = "Qwen 3 235B",
        ["zai-glm-4.7"] = "ZAI GLM 4.7",
    };

    // These reason by default, and reasoning tokens count against max_completion_tokens.
    private static readonly HashSet<string> s_reasoningModels =
        new([DefaultModelId, "qwen-3.8-27b", "zai-glm-4.7", "kimi-k2.7-code"], StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly Lock _catalogLock = new();
    private IPluginHostServices? _host;
    private bool _streamResponses = true;
    private string _temperatureMode = CustomTemperatureMode;
    private double _temperature = DefaultTemperature;

    // Bumped on key change or deactivation so an in-flight refresh cannot commit.
    private long _catalogGeneration;

    public CerebrasPlugin()
        : this(new HttpClient())
    {
    }

    internal CerebrasPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.cerebras";
    public string PluginName => "Cerebras";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingKey));
        _temperature = host.GetSetting<double?>(TemperatureSettingKey) is { } saved && IsTemperature(saved)
            ? saved
            : DefaultTemperature;
        lock (_catalogLock)
            SetCatalog(LoadSavedCatalog(host, ApiKey));
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

    public string ProviderName => "Cerebras";
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

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
            ApiKey!,
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
            ApiKey!,
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
            MaxOutputTokenParameter = "max_completion_tokens",
            MaxOutputTokens = s_reasoningModels.Contains(modelId)
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
            Temperature = _temperatureMode == CustomTemperatureMode ? _temperature : null,
        };

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
        var trimmed = apiKey.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        // The saved catalog is keyed to its owner, so a failed secret write leaves the old pair intact.
        var host = _host;
        if (host is not null)
        {
            if (normalized is null)
                await host.DeleteSecretAsync("api-key");
            else
                await host.StoreSecretAsync("api-key", normalized);
        }

        if (!string.Equals(ApiKey, normalized, StringComparison.Ordinal))
        {
            lock (_catalogLock)
            {
                _catalogGeneration++;
                ApiKey = normalized;
                SetCatalog(host is null ? [] : LoadSavedCatalog(host, normalized));
            }
        }

        host?.NotifyCapabilitiesChanged();
    }

    // A catalog saved for a different key is ignored.
    private static List<string> LoadSavedCatalog(IPluginHostServices host, string? apiKey) =>
        host.GetSetting<CerebrasModelCatalog>(FetchedModelsSettingKey) is { } saved && saved.Owner == Fingerprint(apiKey)
            ? NormalizeModels(saved.Models ?? [])
            : [];

    internal static string Fingerprint(string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"TypeWhisper.Cerebras.catalog/1\n{apiKey}")));

    /// <summary>
    ///     Lists the models available to the key. An empty, malformed or oversized catalog is
    ///     rejected whole, so a partial list is never saved.
    /// </summary>
    internal async Task<List<string>> FetchModelsAsync(string apiKey, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDiscoveryDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            return ParseModelCatalog(body) ?? throw InvalidCatalog();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "Loading Cerebras models timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    private static List<string>? ParseModelCatalog(string body)
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
                || data.GetArrayLength() is 0 or > MaxCatalogModels)
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

                ids.Add(id.GetString()!);
            }

            return NormalizeModels(ids);
        }
    }

    private static PluginRequestException InvalidCatalog() =>
        new("Cerebras returned an empty, invalid or oversized model catalog.",
            PluginRequestFailureKind.OutputIncomplete, isTransient: false);

    // Commits only when no key change or deactivation happened since the refresh began.
    private bool TryCommitCatalog(List<string> models, long generation, string apiKey)
    {
        IPluginHostServices host;
        lock (_catalogLock)
        {
            if (_host is null
                || generation != _catalogGeneration
                || !string.Equals(ApiKey, apiKey, StringComparison.Ordinal))
            {
                return false;
            }

            host = _host;
            // Persist first so a failed write leaves the live catalog untouched.
            host.SetSetting(FetchedModelsSettingKey, new CerebrasModelCatalog(Fingerprint(apiKey), models));
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
                .Select(id => new PluginModelInfo(id, s_knownDisplayNames.GetValueOrDefault(id, id)))
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "api-key",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "csk-...",
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
                "api-key" => ApiKey,
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
            case "api-key":
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
        var apiKey = ApiKey;
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
}

internal sealed record CerebrasModelCatalog(string? Owner, List<string>? Models);
