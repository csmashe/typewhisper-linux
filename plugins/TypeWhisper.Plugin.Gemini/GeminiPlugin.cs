// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

public sealed class GeminiPlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware, IModelCatalogProvider
{
    // The shared chat helper appends /v1/... to Google's compatibility base URL.
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string ApiKeySecretName = "api-key";
    private const string FetchedLlmModelsSettingName = "fetchedLlmModels.v2";
    private const string SelectedLlmModelSettingName = "selectedLLMModel";
    // Gemini models answer with visible text plus hidden reasoning; 8192 leaves room for
    // both at low effort without asking a Flash model for more than it can return.
    private const int GeminiMaxOutputTokens = 8192;
    internal const string DefaultModel = "gemini-flash-lite-latest";

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackLlmModels =
    [
        new(DefaultModel, "Gemini Flash Lite Latest") { IsRecommended = true },
        new("gemini-flash-latest", "Gemini Flash Latest"),
        new("gemini-pro-latest", "Gemini Pro Latest"),
        new("gemini-2.5-flash", "Gemini 2.5 Flash"),
        new("gemini-2.5-pro", "Gemini 2.5 Pro"),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite"),
    ];

    private static readonly string[] s_excludedModelTokens =
    [
        "embedding",
        "-image",
        "tts",
        "live",
        "audio",
        "robotics",
        "computer-use",
        "deep-research",
        "omni",
        "vision-only",
        "veo",
        "imagen",
        "aqa",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private long _connectionRevision;
    private List<GeminiFetchedModel> _fetchedLlmModels = [];
    private string? _selectedLlmModel;
    private IPluginHostServices? _host;
    private bool _streamResponses = true;

    public GeminiPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
    {
    }

    internal GeminiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.gemini";
    public string PluginName => "Google Gemini";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        // Trim on load: legacy values saved before SetApiKeyAsync trimmed would
        // otherwise reach the Bearer header with trailing whitespace and 401
        // every request while IsAvailable still reports true.
        var loaded = await host.LoadSecretAsync(ApiKeySecretName);
        ApiKey = string.IsNullOrWhiteSpace(loaded) ? null : loaded.Trim();
        _fetchedLlmModels = NormalizeFetchedLlmModels(
            host.GetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName) ?? []);
        _selectedLlmModel = host.GetSetting<string>(SelectedLlmModelSettingName);
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsAvailable})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderName => "Google Gemini";
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            var models = _fetchedLlmModels;
            if (models.Count == 0)
            {
                var fallbackDefaultId = s_fallbackLlmModels.FirstOrDefault(model => string.Equals(
                    model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id ?? DefaultModel;
                return s_fallbackLlmModels
                    .OrderByDescending(model => string.Equals(
                        model.Id, fallbackDefaultId, StringComparison.OrdinalIgnoreCase))
                    .Select(model => model with
                    {
                        IsRecommended = string.Equals(model.Id, fallbackDefaultId, StringComparison.OrdinalIgnoreCase),
                    })
                    .ToList();
            }

            // Host services use the first model as their implicit default.
            var defaultModelId = models.FirstOrDefault(model => string.Equals(
                model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id ?? ResolveDefaultModelId(models);
            return models
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    defaultModelId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new PluginModelInfo(model.Id, model.DisplayName ?? model.Id)
                {
                    IsRecommended = string.Equals(model.Id, defaultModelId, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
        }
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        model = ResolveRequestModel(model);
        try
        {
            return await OpenAiChatHelper.SendChatCompletionAsync(
                _httpClient,
                BaseUrl,
                ApiKey!,
                model,
                systemPrompt,
                userText,
                CreateRequestOptions(model, systemPrompt, userText),
                ct
            );
        }
        catch (JsonException ex)
        {
            throw new PluginRequestException(
                "The provider returned a malformed response.",
                PluginRequestFailureKind.EmptyResponse,
                innerException: ex);
        }
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
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        model = ResolveRequestModel(model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            model,
            systemPrompt,
            userText,
            CreateRequestOptions(model, systemPrompt, userText),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal IReadOnlyList<GeminiFetchedModel> FetchedLlmModels => _fetchedLlmModels;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        await _configurationGate.WaitAsync();
        try
        {
            var normalized = NormalizeApiKey(apiKey);
            var previousApiKey = ApiKey;
            var wasAvailable = IsAvailable;
            var changed = !string.Equals(previousApiKey, normalized, StringComparison.Ordinal);
            var catalogChanged = changed && _fetchedLlmModels.Count > 0;

            if (_host is not null)
            {
                var secretPersisted = false;
                var catalogWriteAttempted = false;
                try
                {
                    await PersistApiKeyAsync(_host, normalized);
                    secretPersisted = true;
                    if (catalogChanged)
                    {
                        catalogWriteAttempted = true;
                        _host.SetSetting<List<GeminiFetchedModel>>(FetchedLlmModelsSettingName, []);
                    }
                }
                catch (Exception writeException)
                {
                    List<Exception> rollbackFailures = [];
                    if (secretPersisted)
                    {
                        try
                        {
                            await PersistApiKeyAsync(_host, previousApiKey);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (catalogWriteAttempted)
                    {
                        try
                        {
                            _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Failed to persist the API key and restore the previous state.",
                            new AggregateException([writeException, .. rollbackFailures]));
                    }

                    throw;
                }
            }

            ApiKey = normalized;
            if (changed)
                Interlocked.Increment(ref _connectionRevision);
            // The selection deliberately survives a key change: the fallback aliases or the
            // next successful refresh repair it, and re-entering a key keeps the user's choice.
            if (catalogChanged)
                _fetchedLlmModels = [];

            if (_host is not null
                && ((changed && wasAvailable != IsAvailable) || catalogChanged))
            {
                _host.NotifyCapabilitiesChanged();
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal async Task<bool> SetFetchedLlmModelsAsync(
        IEnumerable<GeminiFetchedModel> models,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeFetchedLlmModels(models);
        await _configurationGate.WaitAsync(ct);
        try
        {
            if (expectedRevision is not null
                && Interlocked.Read(ref _connectionRevision) != expectedRevision)
            {
                _host?.Log(PluginLogLevel.Debug, "Discarded a model catalog fetched for a previous API key.");
                return false;
            }

            var catalogChanged = !ModelCatalogsEqual(_fetchedLlmModels, normalized);
            var selectedModel = normalized.FirstOrDefault(model => string.Equals(
                model.Id, _selectedLlmModel, StringComparison.OrdinalIgnoreCase))?.Id
                ?? (normalized.Count > 0 ? ResolveDefaultModelId(normalized) : DefaultModel);
            var selectionChanged = !string.Equals(_selectedLlmModel, selectedModel, StringComparison.Ordinal);
            if (!catalogChanged && !selectionChanged)
                return true;

            if (_host is not null)
            {
                var selectionWriteAttempted = false;
                try
                {
                    if (catalogChanged)
                        _host.SetSetting(FetchedLlmModelsSettingName, normalized);
                    if (selectionChanged)
                    {
                        selectionWriteAttempted = true;
                        _host.SetSetting(SelectedLlmModelSettingName, selectedModel);
                    }
                }
                catch (Exception writeException)
                {
                    try
                    {
                        if (catalogChanged)
                            _host.SetSetting(FetchedLlmModelsSettingName, _fetchedLlmModels);
                        if (selectionWriteAttempted)
                            _host.SetSetting(SelectedLlmModelSettingName, _selectedLlmModel);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "Failed to persist the model catalog and restore the previous value.",
                            new AggregateException(writeException, rollbackException));
                    }

                    throw;
                }
            }

            _fetchedLlmModels = normalized;
            _selectedLlmModel = selectedModel;
            if (catalogChanged)
                _host?.NotifyCapabilitiesChanged();
            return true;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private static Task PersistApiKeyAsync(IPluginHostServices host, string? apiKey) =>
        apiKey is null
            ? host.DeleteSecretAsync(ApiKeySecretName)
            : host.StoreSecretAsync(ApiKeySecretName, apiKey);

    internal async Task<List<GeminiFetchedModel>?> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        await _configurationGate.WaitAsync(ct);
        string? apiKey;
        long revision;
        try
        {
            apiKey = ApiKey;
            revision = Interlocked.Read(ref _connectionRevision);
        }
        finally
        {
            _configurationGate.Release();
        }
        if (apiKey is null)
            return null;

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Model catalog request failed with HTTP status {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var catalog = JsonSerializer.Deserialize<GeminiCompatibleModelsResponse>(json, s_jsonOptions);
            if (catalog?.Data is null)
            {
                _host?.Log(PluginLogLevel.Warning, "Model catalog response did not contain a data array.");
                return null;
            }

            // ReSharper disable once InvertIf -- guard-clause form matches the two bail-outs above it; inverting would bury the success path in an if.
            if (Interlocked.Read(ref _connectionRevision) != revision)
            {
                _host?.Log(PluginLogLevel.Debug, "Discarded a model catalog fetched for a previous API key.");
                return null;
            }

            return NormalizeFetchedLlmModels(catalog.Data.OfType<GeminiCompatibleModel>());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _host?.Log(PluginLogLevel.Warning, "Model catalog request timed out.");
            return null;
        }
        catch (OperationCanceledException)
        {
            _host?.Log(PluginLogLevel.Debug, "Model catalog request was canceled by the caller.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
        catch (JsonException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog parsing failed with {ex.GetType().Name}.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Model catalog request failed with {ex.GetType().Name}.");
            return null;
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"{BaseUrl}/models", normalized);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsCompatibleChatModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var normalized = NormalizeModelId(id);
        if (!normalized.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !s_excludedModelTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiCompatibleModel> models) =>
        NormalizeFetchedLlmModels(models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                model.Id!,
                model.DisplayName)));

    private static List<GeminiFetchedModel> NormalizeFetchedLlmModels(
        IEnumerable<GeminiFetchedModel> models) =>
        models
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- cached catalogs come from JSON settings, where a null array entry deserializes to a null element despite the non-nullable annotation.
            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new GeminiFetchedModel(
                NormalizeModelId(model.Id),
                string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim()))
            .Where(model => IsCompatibleChatModelId(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolveDefaultModelId(List<GeminiFetchedModel> models)
    {
        var alias = models.FirstOrDefault(model => string.Equals(
            model.Id,
            DefaultModel,
            StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
            return alias.Id;

        return models
            .Where(model => model.Id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.Id.Contains("preview", StringComparison.OrdinalIgnoreCase)
                || model.Id.Contains("experimental", StringComparison.OrdinalIgnoreCase)
                || model.Id.Contains("-exp", StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? 0
                : model.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 1
                : model.Id.Contains("pro", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenByDescending(model => GetModelVersion(model.Id))
            .ThenByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? models[0].Id;
    }

    private static Version GetModelVersion(string id)
    {
        const string prefix = "gemini-";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new Version(0, 0);

        var versionEnd = id.IndexOf('-', prefix.Length);
        if (versionEnd <= prefix.Length)
            return new Version(0, 0);

        var versionText = id[prefix.Length..versionEnd];
        if (!versionText.Contains('.'))
            versionText += ".0";

        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(0, 0);
    }

    private static bool ModelCatalogsEqual(
        List<GeminiFetchedModel> first,
        List<GeminiFetchedModel> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));

    private static string NormalizeModelId(string id)
    {
        var normalized = id.Trim();
        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized["models/".Length..]
            : normalized;
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    public void Dispose()
    {
        _configurationGate.Dispose();
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: ApiKeySecretName,
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "AIza...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: SelectedLlmModelSettingName,
                Label: Loc.L("Settings.LlmModel"),
                Description: _fetchedLlmModels.Count > 0
                    ? Loc.L("Settings.LlmModelDescriptionFetched", _fetchedLlmModels.Count)
                    : Loc.L("Settings.LlmModelDescriptionDefault"),
                Options: SupportedModels.Select(model => new PluginSettingOption(model.Id, model.DisplayName)).ToList(),
                Kind: PluginSettingKind.Dropdown
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
                ApiKeySecretName => ApiKey,
                SelectedLlmModelSettingName => _selectedLlmModel ?? SupportedModels[0].Id,
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
            case ApiKeySecretName:
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case SelectedLlmModelSettingName:
                await SelectLlmModelAsync(value, ct);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    // Runs under the configuration gate so a settings save cannot interleave with a
    // catalog refresh; ids outside the catalog fall back to the recommended model like
    // the OpenAI plugin does.
    private async Task SelectLlmModelAsync(string? value, CancellationToken ct)
    {
        await _configurationGate.WaitAsync(ct);
        try
        {
            var requested = string.IsNullOrWhiteSpace(value) ? null : NormalizeModelId(value);
            var modelId = requested is null
                ? null
                : SupportedModels.FirstOrDefault(model =>
                        string.Equals(model.Id, requested, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? SupportedModels[0].Id;
            _host?.SetSetting(SelectedLlmModelSettingName, modelId);
            _selectedLlmModel = modelId;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // IModelCatalogProvider contract: a failed or empty fetch must leave the cached
    // catalog and the selection untouched; only a non-empty response replaces them.
    public async Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        await RefreshCatalogAsync(ct);
    }

    private async Task<int?> RefreshCatalogAsync(CancellationToken ct, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? Interlocked.Read(ref _connectionRevision);
        var models = await FetchLlmModelsAsync(ct);
        if (models is not { Count: > 0 })
            return null;

        return await SetFetchedLlmModelsAsync(models, revision, ct) ? models.Count : null;
    }

    private string ResolveRequestModel(string model) =>
        string.IsNullOrWhiteSpace(model)
            ? _selectedLlmModel ?? SupportedModels[0].Id
            : NormalizeModelId(model);

    private static OpenAiChatRequestOptions CreateRequestOptions(
        string model, string systemPrompt, string userText)
    {
        var usesReasoning = model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
        return new OpenAiChatRequestOptions
        {
            ReasoningEffort = usesReasoning ? "low" : null,
            MaxOutputTokens = usesReasoning
                ? GeminiMaxOutputTokens
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
        };
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        await _configurationGate.WaitAsync(ct);
        string? apiKey;
        long revision;
        try
        {
            apiKey = ApiKey;
            revision = Interlocked.Read(ref _connectionRevision);
        }
        finally
        {
            _configurationGate.Release();
        }
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        if (!await ValidateApiKeyAsync(apiKey, ct)
            || Interlocked.Read(ref _connectionRevision) != revision)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var count = await RefreshCatalogAsync(ct, revision);
        if (Interlocked.Read(ref _connectionRevision) != revision)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        return new PluginSettingsValidationResult(true, count is not null
            ? Loc.L("Settings.ApiKeyValidFetched", count.Value)
            : Loc.L("Settings.ApiKeyValid"));
    }
}

internal sealed record GeminiFetchedModel(string Id, string? DisplayName);

internal sealed record GeminiCompatibleModelsResponse(
    [property: JsonPropertyName("data")] List<GeminiCompatibleModel?>? Data);

// ReSharper disable once ClassNeverInstantiated.Global -- deserialized from the models endpoint
internal sealed record GeminiCompatibleModel(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);
