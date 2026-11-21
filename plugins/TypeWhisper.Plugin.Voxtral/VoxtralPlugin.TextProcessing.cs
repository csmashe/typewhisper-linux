using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

// Mistral chat completions for text processing, sharing the Voxtral API key.
public sealed partial class VoxtralPlugin
{
    private const string TextModelsSettingKey = "fetchedTextModels";
    private const string TemperatureModeSettingKey = "temperatureMode";
    private const string TemperatureSettingKey = "temperature";
    private const string ProviderDefaultTemperatureMode = "provider-default";
    private const string CustomTemperatureMode = "custom";
    private const double DefaultCustomTemperature = 0.3;
    private const string ReasoningEffortSettingKey = "reasoningEffort";
    private const string FastestReasoningEffort = "fastest";
    private const string HighReasoningEffort = "high";

    private const int MaxCatalogModels = 1000;

    private readonly Lock _catalogLock = new();
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private bool _streamResponses = true;
    private string _temperatureMode = ProviderDefaultTemperatureMode;
    private double _temperature = DefaultCustomTemperature;
    private string _reasoningEffort = FastestReasoningEffort;
    private IReadOnlyDictionary<string, string> _textAliases = MistralCatalog.DefaultTextAliases;
    // Whether the account's catalog, not the default lineup, supplied the aliases.
    private bool _textCatalogValidated;

    // Bumped on key change or deactivation so an in-flight refresh cannot commit.
    private long _catalogGeneration;

    public string ProviderName => "Mistral";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; private set; } = FallbackTextModels();

    // Discovery is bounded as a whole, not only by the HTTP client timeout.
    internal TimeSpan ModelDiscoveryDeadline { get; init; } = TimeSpan.FromSeconds(60);

    private void ActivateTextRole(IPluginHostServices host)
    {
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingKey));
        _temperature = host.GetSetting<double?>(TemperatureSettingKey) is { } saved && IsTemperature(saved)
            ? saved
            : DefaultCustomTemperature;
        _reasoningEffort = NormalizeReasoningEffort(host.GetSetting<string>(ReasoningEffortSettingKey));
        lock (_catalogLock)
            SetTextCatalog(LoadSavedTextCatalog(host, ApiKey));
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveTextModelId(model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateChatRequestOptions(modelId, systemPrompt, userText),
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
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveTextModelId(model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateChatRequestOptions(modelId, systemPrompt, userText),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    // A workflow or saved selection may name an alias or a retired model; send the listed one.
    // An unknown model goes as given, so Mistral decides whether the account can use it.
    private string ResolveTextModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return SupportedModels[0].Id;
        var (listed, aliases, _) = TextCatalogSnapshot();
        return MistralCatalog.Resolve(model, listed, aliases) ?? model.Trim();
    }

    /// <summary>
    ///     The listed model a saved selection now names, or null. The host may save the answer, so
    ///     until the account's catalog is validated only this plugin's former default IDs move.
    /// </summary>
    public string? ResolveSavedModelId(string modelId)
    {
        var (listed, aliases, validated) = TextCatalogSnapshot();
        if (validated)
            return MistralCatalog.Resolve(modelId, listed, aliases);

        var raw = modelId.Trim();
        if (listed.Contains(raw))
            return raw;
        return MistralCatalog.FormerDefaultTextModels.TryGetValue(raw, out var id) && listed.Contains(id) ? id : null;
    }

    // One consistent view of the fields SetTextCatalog replaces together.
    private (string[] Listed, IReadOnlyDictionary<string, string> Aliases, bool Validated) TextCatalogSnapshot()
    {
        lock (_catalogLock)
            return (SupportedModels.Select(m => m.Id).ToArray(), _textAliases, _textCatalogValidated);
    }

    /// <summary>The level for the resolved <paramref name="modelId" />; null for models that reject it or unvalidated aliases.</summary>
    internal string? ReasoningEffortFor(string modelId)
    {
        var efforts = MistralCatalog.ReasoningEfforts(modelId);
        if (efforts.Length == 0)
            return null;
        return _reasoningEffort == HighReasoningEffort && efforts.Contains(HighReasoningEffort)
            ? HighReasoningEffort
            : efforts[0];
    }

    // Mistral rejects prompt + max_tokens beyond the context, so only models that reason get the reserve.
    private OpenAiChatRequestOptions CreateChatRequestOptions(string modelId, string systemPrompt, string userText)
    {
        var effort = ReasoningEffortFor(modelId);
        var reasons = effort is not (null or "none") || modelId.StartsWith("magistral-", StringComparison.Ordinal);
        return new OpenAiChatRequestOptions
        {
            ProviderName = ProviderName,
            MaxOutputTokens = reasons
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
            ReasoningEffort = effort,
            Temperature = _temperatureMode == CustomTemperatureMode ? _temperature : null,
        };
    }

    // A catalog saved for a different key is ignored.
    private static MistralTextModelCatalog? LoadSavedTextCatalog(IPluginHostServices host, string? apiKey) =>
        host.GetSetting<MistralTextModelCatalog>(TextModelsSettingKey) is { } saved && saved.Owner == Fingerprint(apiKey)
            ? saved
            : null;

    internal static string Fingerprint(string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"TypeWhisper.Mistral.textCatalog/1\n{apiKey}")));

    /// <summary>
    ///     Lists the chat-completion models available to the key. An empty, malformed or oversized
    ///     catalog is rejected whole, so a partial list is never saved.
    /// </summary>
    internal async Task<MistralTextModelCatalog> FetchTextModelsAsync(string apiKey, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDiscoveryDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, deadline.Token,
                MistralErrorHandler.Describe);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            return MistralCatalog.ParseText(body, Fingerprint(apiKey), MaxCatalogModels) ?? throw InvalidTextCatalog();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                Loc.L("Errors.ModelRefreshTimedOut"), PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    private PluginRequestException InvalidTextCatalog() =>
        new(Loc.L("Errors.ModelCatalogInvalid"),
            PluginRequestFailureKind.OutputIncomplete, isTransient: false);

    // Commits only when no key change or deactivation happened since the refresh began.
    private bool TryCommitTextCatalog(MistralTextModelCatalog catalog, long generation, string apiKey)
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
            host.SetSetting(TextModelsSettingKey, catalog);
            SetTextCatalog(catalog);
        }

        host.NotifyCapabilitiesChanged();
        return true;
    }

    // Caller holds _catalogLock. Known general models lead, default first, for blank-model requests.
    private void SetTextCatalog(MistralTextModelCatalog? catalog)
    {
        var models = NormalizeModels(catalog?.Models ?? []);
        _textCatalogValidated = models.Count > 0 && catalog!.Version == MistralCatalog.CurrentVersion;
        if (models.Count == 0)
        {
            _textAliases = MistralCatalog.DefaultTextAliases;
            SupportedModels = FallbackTextModels();
            return;
        }

        if (catalog!.Version == MistralCatalog.CurrentVersion)
        {
            // Only the account's own aliases: a "-latest" key may point elsewhere for this account.
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, id) in catalog.Aliases ?? [])
            {
                if (MistralCatalog.IsModelId(alias) && MistralCatalog.IsModelId(id))
                    aliases[alias] = id;
            }

            _textAliases = aliases;
            SupportedModels = models
                .Select(id => new PluginModelInfo(id, MistralCatalog.DisplayName(id, catalog.Names)))
                .ToList();
            return;
        }

        // A list saved by an earlier version holds the raw account IDs; it stays until the next validation.
        _textAliases = MistralCatalog.DefaultTextAliases;
        SupportedModels = models
            .OrderBy(id => MistralCatalog.Rank(id, _textAliases))
            .ThenBy(id => id, StringComparer.Ordinal)
            .Select(id => new PluginModelInfo(id, MistralCatalog.DisplayName(_textAliases.GetValueOrDefault(id, id), null)))
            .ToList();
    }

    private static List<PluginModelInfo> FallbackTextModels() =>
        MistralCatalog.DefaultTextModels.Select(id => new PluginModelInfo(id, MistralCatalog.DisplayName(id, null))).ToList();

    private static List<string> NormalizeModels(IEnumerable<string?> models) =>
        models
            .Where(MistralCatalog.IsModelId)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private void SetReasoningEffort(string? value)
    {
        var effort = NormalizeReasoningEffort(value);
        _host?.SetSetting(ReasoningEffortSettingKey, effort);
        _reasoningEffort = effort;
    }

    private static string NormalizeReasoningEffort(string? value) =>
        value == HighReasoningEffort ? HighReasoningEffort : FastestReasoningEffort;

    private void SetTemperatureMode(string? value)
    {
        var mode = NormalizeTemperatureMode(value);
        _host?.SetSetting(TemperatureModeSettingKey, mode);
        _temperatureMode = mode;
    }

    private void SetTemperature(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || !IsTemperature(temperature))
        {
            throw new ArgumentException(Loc.L("Settings.TemperatureInvalid"));
        }

        _host?.SetSetting(TemperatureSettingKey, temperature);
        _temperature = temperature;
    }

    private static string NormalizeTemperatureMode(string? value) =>
        value == CustomTemperatureMode ? CustomTemperatureMode : ProviderDefaultTemperatureMode;

    // Mistral's chat-completions schema caps temperature at 1.5.
    private static bool IsTemperature(double value) => double.IsFinite(value) && value is >= 0 and <= 1.5;

    private void SetStreamResponses(bool enabled)
    {
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
        _streamResponses = enabled;
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
