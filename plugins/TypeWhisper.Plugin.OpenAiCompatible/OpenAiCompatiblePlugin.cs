// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed class OpenAiCompatiblePlugin
    : ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IModelCatalogProvider,
        IPluginLocalizationAware,
        IPluginCollectionSettingsProvider,
        IAdditionalTranscriptionEnginesProvider,
        IAdditionalLlmProvidersProvider
{
    // Additional named endpoints ("profiles") layered on top of the default endpoint.
    // Each becomes its own selectable transcription engine / LLM provider via the
    // role wrapper below. The default endpoint keeps using the original flat settings
    // keys (baseUrl/api-key/selectedModel/...) so existing single-endpoint setups are
    // unchanged.
    private const string AdditionalProfilesSettingKey = "additionalProfiles";
    private const string ProfilesCollectionKey = "profiles";
    private const string ProfileIdPrefix = "openai-compatible-";

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private List<FetchedModel> _fetchedModels = [];
    private bool _streamResponses = true;
    private readonly List<OpenAiCompatibleProfile> _additionalProfiles = [];
    private readonly Dictionary<string, string?> _additionalApiKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenAiCompatibleProfileRole> _profileRoles =
        new(StringComparer.Ordinal);

    // Guards _profileRoles: the capability getters populate it lazily (a read that
    // mutates) while model-selection, catalog refresh, and invalidation remove from it,
    // and these run on different threads (host capability rebuilds vs. UI/async paths).
    private readonly Lock _profileRolesLock = new();

    public OpenAiCompatiblePlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
    }

    internal OpenAiCompatiblePlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.openai-compatible";
    public string PluginName => "OpenAI Compatible";
    public string PluginVersion => "1.0.1";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        BaseUrl = host.GetSetting<string>("baseUrl");
        SelectedModelId = host.GetSetting<string>("selectedModel");
        SelectedLlmModelId = host.GetSetting<string>("selectedLlmModel");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;

        var modelsJson = host.GetSetting<string>("fetchedModels");
        if (!string.IsNullOrEmpty(modelsJson))
        {
            try
            {
                _fetchedModels = JsonSerializer.Deserialize<List<FetchedModel>>(modelsJson) ?? [];
            }
            catch
            {
                _fetchedModels = [];
            }
        }

        await LoadAdditionalProfilesAsync(host);

        // Don't log the raw base URL: it is user-supplied and could carry credentials
        // (userinfo or a query token) into shared log/support bundles. IsConfigured
        // already conveys whether an endpoint is set.
        host.Log(
            PluginLogLevel.Info,
            $"Activated (configured={IsConfigured}, additionalProfiles={_additionalProfiles.Count})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "openai-compatible";
    public string ProviderDisplayName => "Custom Server";

    public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var models = _fetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(SelectedModelId))
                return [new PluginModelInfo(SelectedModelId, SelectedModelId)];

            return models;
        }
    }

    public string? SelectedModelId { get; private set; }

    public void SelectModel(string modelId)
    {
        SelectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public bool SupportsTranslation => true;

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));
        if (string.IsNullOrEmpty(SelectedModelId))
            throw new InvalidOperationException(Loc.L("Settings.NoTranscriptionModelSelected"));

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            BaseUrl!,
            ApiKey ?? "",
            SelectedModelId!,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt
        );
    }

    public string ProviderName => "OpenAI Compatible";

    public bool IsAvailable => IsConfigured && !string.IsNullOrEmpty(SelectedLlmModelId);

    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            var models = _fetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(SelectedLlmModelId))
                return [new PluginModelInfo(SelectedLlmModelId, SelectedLlmModelId)];

            return models;
        }
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));

        var modelId = !string.IsNullOrEmpty(model) ? model : SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException(Loc.L("Settings.NoLlmModelSelected"));

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl!,
            ApiKey ?? "",
            modelId,
            systemPrompt,
            userText,
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

        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));

        var modelId = !string.IsNullOrEmpty(model) ? model : SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException(Loc.L("Settings.NoLlmModelSelected"));

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl!,
            ApiKey ?? "",
            modelId,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    internal string? BaseUrl { get; private set; }

    internal string? ApiKey { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;
    internal string? SelectedTranscriptionModelId => SelectedModelId;
    internal string? SelectedLlmModelId { get; private set; }

    internal IReadOnlyList<FetchedModel> FetchedModels => _fetchedModels;

    internal void SetBaseUrl(string url)
    {
        // Helpers append "/v1/..." themselves; pasted URLs often already
        // include "/v1", so strip a trailing "/v1" segment to avoid building
        // "/v1/v1/models" and similar paths.
        var normalized = NormalizeBaseUrl(url);
        var changed = !string.Equals(BaseUrl, normalized, StringComparison.Ordinal);
        BaseUrl = normalized;
        _host?.SetSetting("baseUrl", normalized);

        if (changed)
            SetFetchedModels([]);
        else
            _host?.NotifyCapabilitiesChanged();
    }

    internal async Task SetApiKeyAsync(string key)
    {
        var apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
        var changed = !string.Equals(ApiKey, apiKey, StringComparison.Ordinal);

        if (_host is not null)
        {
            if (apiKey is null)
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);
        }

        ApiKey = apiKey;
        if (changed)
            SetFetchedModels([]);
        else
            _host?.NotifyCapabilitiesChanged();
    }

    internal void SelectLlmModel(string modelId)
    {
        SelectedLlmModelId = modelId;
        _host?.SetSetting("selectedLlmModel", modelId);
    }

    internal void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal void SetFetchedModels(List<FetchedModel> models, bool notifyCapabilitiesChanged = true)
    {
        var selectedModelId = NormalizeModelSelection(SelectedModelId, models);
        var selectedLlmModelId = NormalizeModelSelection(SelectedLlmModelId, models);

        _fetchedModels = models;
        SelectedModelId = selectedModelId;
        SelectedLlmModelId = selectedLlmModelId;

        try
        {
            var json = JsonSerializer.Serialize(models);
            _host?.SetSetting("fetchedModels", json);
        }
        catch
        { /* best effort */
        }

        _host?.SetSetting("selectedModel", selectedModelId);
        _host?.SetSetting("selectedLlmModel", selectedLlmModelId);

        if (notifyCapabilitiesChanged)
            _host?.NotifyCapabilitiesChanged();
    }

    // Returns null on a fetch/parse failure (so callers can keep their cached
    // list), and a (possibly empty) list on a successful /v1/models response —
    // an empty list is a valid "this server has zero models" answer, distinct
    // from "couldn't reach/parse the server."
    internal async Task<List<FetchedModel>?> FetchModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            return data.EnumerateArray()
                .Select(e => new FetchedModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null
                ))
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .OrderBy(m => m.Id)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "baseUrl",
                Loc.L("Settings.BaseUrl"),
                false,
                "http://localhost:8000",
                Loc.L("Settings.BaseUrlDescription")
            ),
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                null,
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: _fetchedModels.Count > 0
                    ? Loc.L("Settings.ModelsFetched", _fetchedModels.Count)
                    : Loc.L("Settings.ValidateToFetchModels"),
                Options: BuildModelOptions()
            ),
            new(
                "selectedLlmModel",
                Loc.L("Settings.LlmModel"),
                Description: _fetchedModels.Count > 0
                    ? Loc.L("Settings.ModelsFetched", _fetchedModels.Count)
                    : Loc.L("Settings.ValidateToFetchModels"),
                Options: BuildModelOptions()
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
                "baseUrl" => BaseUrl,
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
            case "baseUrl":
                SetBaseUrl(value ?? string.Empty);
                break;
            case "api-key":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            // Only honor a selection that exists in the authoritative catalog. On the
            // host's full-form save, an earlier baseUrl/api-key change clears the catalog
            // and both selections, but the form still carries the previous selection in
            // these later fields; restoring it unconditionally would re-pair the new
            // endpoint with a stale model. A genuine dropdown pick is always in-catalog.
            case "selectedModel":
                if (IsKnownModel(value))
                    SelectModel(value!);
                break;
            case "selectedLlmModel":
                if (IsKnownModel(value))
                    SelectLlmModel(value!);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    private bool IsKnownModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && _fetchedModels.Any(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterBaseUrl"));

        var models = await FetchModelsAsync(ct);
        if (models is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.CouldNotConnect"));

        SetFetchedModels(models, notifyCapabilitiesChanged: false);
        _host?.NotifyCapabilitiesChanged();

        return new PluginSettingsValidationResult(
            true,
            Loc.L("Settings.ConnectionOk", models.Count)
        );
    }

    // IModelCatalogProvider: model-list refresh for dropdown-open. A successful
    // response is authoritative for both the catalog and selections; a transient
    // failure leaves all three untouched.
    public async Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(BaseUrl))
        {
            var models = await FetchModelsAsync(ct);
            if (models is not null && DefaultCatalogStateChanged(models))
                SetFetchedModels(models);
        }

        // Refresh additional profiles on the same dropdown-open path so their
        // catalogs don't go stale when a server adds or removes models after the
        // profile was first saved.
        var changedProfileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in _additionalProfiles.Where(p => !string.IsNullOrEmpty(p.BaseUrl)))
        {
            var models = await FetchModelsForAsync(profile.BaseUrl, GetProfileApiKey(profile.Id), ct);
            if (models is null || !ProfileCatalogStateChanged(profile, models))
                continue;

            ApplyProfileCatalog(profile, models);
            changedProfileIds.Add(profile.Id);
        }

        if (changedProfileIds.Count > 0)
        {
            PersistAdditionalProfiles(notify: false);
            lock (_profileRolesLock)
            {
                foreach (var id in changedProfileIds)
                    _profileRoles.Remove(id);
            }

            _host?.NotifyCapabilitiesChanged();
        }
    }

    private static bool CatalogChanged(List<FetchedModel> fetched, List<FetchedModel> current) =>
        !fetched.SequenceEqual(current);

    private bool DefaultCatalogStateChanged(List<FetchedModel> models) =>
        CatalogChanged(models, _fetchedModels)
        || !string.Equals(
            SelectedModelId,
            NormalizeModelSelection(SelectedModelId, models),
            StringComparison.Ordinal
        )
        || !string.Equals(
            SelectedLlmModelId,
            NormalizeModelSelection(SelectedLlmModelId, models),
            StringComparison.Ordinal
        );

    private static bool ProfileCatalogStateChanged(
        OpenAiCompatibleProfile profile,
        List<FetchedModel> models
    ) =>
        CatalogChanged(models, profile.FetchedModels)
        || !string.Equals(
            profile.SelectedModelId,
            NormalizeModelSelection(profile.SelectedModelId, models),
            StringComparison.Ordinal
        )
        || !string.Equals(
            profile.SelectedLlmModelId,
            NormalizeModelSelection(profile.SelectedLlmModelId, models),
            StringComparison.Ordinal
        );

    private static void ApplyProfileCatalog(
        OpenAiCompatibleProfile profile,
        List<FetchedModel> models
    )
    {
        profile.SelectedModelId = NormalizeModelSelection(profile.SelectedModelId, models);
        profile.SelectedLlmModelId = NormalizeModelSelection(profile.SelectedLlmModelId, models);
        profile.FetchedModels = models;
    }

    private static string? NormalizeModelSelection(
        string? selectedModelId,
        List<FetchedModel> models
    )
    {
        if (models.Count == 0)
            return null;

        return !string.IsNullOrWhiteSpace(selectedModelId)
            && models.Any(m => string.Equals(m.Id, selectedModelId, StringComparison.Ordinal))
                ? selectedModelId
                : models[0].Id;
    }

    private List<PluginSettingOption>? BuildModelOptions()
    {
        var models = _fetchedModels.Select(m => new PluginSettingOption(m.Id, m.Id)).ToList();

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (models.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(SelectedModelId))
                models.Add(new PluginSettingOption(SelectedModelId, SelectedModelId));
            if (
                !string.IsNullOrWhiteSpace(SelectedLlmModelId)
                && models.All(m => m.Value != SelectedLlmModelId)
            )
                models.Add(new PluginSettingOption(SelectedLlmModelId, SelectedLlmModelId));
        }

        return models.Count > 0 ? models : null;
    }

    // ---- Additional provider profiles ----------------------------------------
    // The default endpoint above is untouched. Everything below adds extra named
    // endpoints, each surfaced as its own selectable transcription engine / LLM
    // provider via OpenAiCompatibleProfileRole and the selection-identity scheme.

    public IReadOnlyList<ITranscriptionEngineRole> AdditionalTranscriptionEngines =>
        _additionalProfiles
            .Select(ITranscriptionEngineRole (profile) => GetProfileRole(profile.Id))
            .ToList();

    public IReadOnlyList<ILlmProviderRole> AdditionalLlmProviders =>
        _additionalProfiles
            .Select(ILlmProviderRole (profile) => GetProfileRole(profile.Id))
            .ToList();

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
        [
            new(
                Key: ProfilesCollectionKey,
                Label: Loc.L("Settings.ProfilesLabel"),
                Description: Loc.L("Settings.ProfilesDescription"),
                ItemFields:
                [
                    new PluginSettingDefinition(
                        "name", Loc.L("Settings.ProfileName"),
                        Placeholder: Loc.L("Settings.ProfileNamePlaceholder"),
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "baseUrl", Loc.L("Settings.BaseUrl"), Placeholder: "http://localhost:11434",
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "api-key", Loc.L("Settings.ApiKey"), IsSecret: true,
                        Description: Loc.L("Settings.ProfileApiKeyDescription"),
                        Kind: PluginSettingKind.Secret),
                    new PluginSettingDefinition(
                        "selectedModel", Loc.L("Settings.TranscriptionModel"),
                        Description: Loc.L("Settings.ProfileTranscriptionModelDescription"),
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "selectedLlmModel", Loc.L("Settings.LlmModel"),
                        Description: Loc.L("Settings.ProfileLlmModelDescription"),
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
                ],
                ItemLabelFieldKey: "name",
                AddButtonLabel: Loc.L("Settings.AddProfile")
            ),
        ];

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    )
    {
        if (collectionKey != ProfilesCollectionKey)
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);

        IReadOnlyList<PluginCollectionItem> items = _additionalProfiles
            .Select(p => new PluginCollectionItem(
                new Dictionary<string, string?>
                {
                    ["name"] = p.Name,
                    ["baseUrl"] = p.BaseUrl,
                    // Secrets are never echoed back to the UI.
                    ["api-key"] = null,
                    ["selectedModel"] = p.SelectedModelId,
                    ["selectedLlmModel"] = p.SelectedLlmModelId,
                    ["__id"] = p.Id,
                }
            ))
            .ToList();

        return Task.FromResult(items);
    }

    public async Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    )
    {
        if (collectionKey != ProfilesCollectionKey)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.UnknownCollection"));

        var previousById = _additionalProfiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var newProfiles = new List<OpenAiCompatibleProfile>(items.Count);
        var keyUpdates = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var name = (Get(item, "name") ?? "").Trim();
            var label = name.Length == 0 ? "(unnamed)" : name;

            var rawUrl = (Get(item, "baseUrl") ?? "").Trim();
            if (rawUrl.Length == 0)
                return new PluginSettingsValidationResult(false, $"Profile '{label}': base URL is required.");

            var baseUrl = NormalizeBaseUrl(rawUrl);
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return new PluginSettingsValidationResult(
                    false, $"Profile '{label}': base URL must be an absolute http:// or https:// URL.");
            }

            var id = NormalizeProfileId(Get(item, "__id"), seenIds);

            var submittedKey = Get(item, "api-key");
            var key = NullIfWhiteSpace(submittedKey);
            var storedKey = GetProfileApiKey(id);
            var keyChanged = false;
            if (submittedKey is not null)
            {
                if (key is null)
                {
                    if (storedKey is not null)
                    {
                        keyUpdates[id] = null;
                        keyChanged = true;
                    }
                }
                else if (!string.Equals(storedKey, key, StringComparison.Ordinal))
                {
                    keyUpdates[id] = key;
                    keyChanged = true;
                }
            }

            // A changed endpoint (base URL or credentials) may point at a different
            // server, so drop the stale catalog and this save's submitted selections
            // rather than pair the new endpoint with the previous server's model IDs.
            var hadProfile = previousById.TryGetValue(id, out var prev);
            var endpointChanged = hadProfile
                && (keyChanged
                    || !string.Equals(prev!.BaseUrl, baseUrl, StringComparison.Ordinal));
            var preserveCatalog = hadProfile && !endpointChanged;
            var selectedModelId = endpointChanged
                ? null
                : NullIfWhiteSpace(Get(item, "selectedModel"));
            var selectedLlmModelId = endpointChanged
                ? null
                : NullIfWhiteSpace(Get(item, "selectedLlmModel"));

            newProfiles.Add(new OpenAiCompatibleProfile
            {
                Id = id,
                Name = name.Length == 0 ? "Custom Server" : name,
                BaseUrl = baseUrl,
                SelectedModelId = selectedModelId,
                SelectedLlmModelId = selectedLlmModelId,
                FetchedModels = preserveCatalog ? prev!.FetchedModels : [],
            });
        }

        // Secrets and profile metadata are separate writes and the host store has no multi-key
        // transaction. Persisting a rotated key but not the endpoint it belongs to would, after a
        // restart, send the new credential to the profile's previous URL — so every committed step
        // records its inverse and any failure replays them in reverse. Compensation goes through
        // the same store, so a persistent failure (read-only mount, disk full) can still leave the
        // two out of step; that is logged, and closing it needs an atomic multi-key host API.
        var previousProfiles = _additionalProfiles.ToList();
        var undo = new List<Func<Task>>();
        try
        {
            if (_host is not null)
            {
                foreach (var removedId in previousById.Keys.Where(k => !seenIds.Contains(k)))
                    await ApplyProfileSecretAsync(removedId, null, undo);

                foreach (var (id, key) in keyUpdates)
                    await ApplyProfileSecretAsync(id, key, undo);
            }

            _additionalProfiles.Clear();
            _additionalProfiles.AddRange(newProfiles);
            undo.Add(() =>
            {
                _additionalProfiles.Clear();
                _additionalProfiles.AddRange(previousProfiles);
                PersistAdditionalProfiles(notify: false);
                return Task.CompletedTask;
            });

            // State is now persisted; the best-effort model fetch below may fail or be
            // cancelled, but that must not revert the saved profiles.
            PersistAdditionalProfiles(notify: false);
        }
        catch
        {
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    await undo[i]();
                }
                catch (Exception rollbackFailure)
                {
                    // Nothing better to try: report the original failure, not this one.
                    _host?.Log(
                        PluginLogLevel.Error,
                        $"Could not roll back a profile save: {rollbackFailure.Message}"
                    );
                }
            }

            throw;
        }

        InvalidateChangedProfileRoles(previousById, keyUpdates.Keys);

        // Best-effort: populate model catalogs so prompts/dictation can list each
        // profile's models. New profiles and profiles whose endpoint changed have an
        // empty catalog here and get (re)fetched; an unchanged endpoint keeps its
        // existing catalog and is skipped.
        foreach (var profile in _additionalProfiles.Where(p => p.FetchedModels.Count == 0))
        {
            var models = await FetchModelsForAsync(profile.BaseUrl, GetProfileApiKey(profile.Id), ct);
            if (models is not null)
                ApplyProfileCatalog(profile, models);
        }

        PersistAdditionalProfiles(notify: false);
        InvalidateChangedProfileRoles(previousById, keyUpdates.Keys);
        _host?.NotifyCapabilitiesChanged();

        return new PluginSettingsValidationResult(true, $"Saved {_additionalProfiles.Count} profile(s).");
    }

    /// <summary>
    ///     Writes one profile's API key (<paramref name="key" /> null deletes it) and appends the
    ///     inverse operation to <paramref name="undo" />. The cache mutation stays paired with the
    ///     host op so the two can never disagree, in either direction.
    /// </summary>
    private async Task ApplyProfileSecretAsync(
        string id,
        string? key,
        List<Func<Task>> undo)
    {
        var host = _host!;
        var secretKey = SecretKeyFor(id);
        var hadPrevious = _additionalApiKeys.TryGetValue(id, out var previous);

        if (key is null)
        {
            await host.DeleteSecretAsync(secretKey);
            _additionalApiKeys.Remove(id);
        }
        else
        {
            await host.StoreSecretAsync(secretKey, key);
            _additionalApiKeys[id] = key;
        }

        undo.Add(async () =>
        {
            if (hadPrevious && previous is not null)
            {
                await host.StoreSecretAsync(secretKey, previous);
                _additionalApiKeys[id] = previous;
            }
            else
            {
                await host.DeleteSecretAsync(secretKey);
                if (hadPrevious)
                    _additionalApiKeys[id] = null;
                else
                    _additionalApiKeys.Remove(id);
            }
        });
    }

    internal string ProfileDisplayName(string id) => FindAdditional(id)?.DisplayName ?? "Custom Server";

    internal bool ProfileConfigured(string id) => !string.IsNullOrEmpty(FindAdditional(id)?.BaseUrl);

    internal bool ProfileLlmAvailable(string id) => ProfileConfigured(id) && ProfileLlmModels(id).Count > 0;

    internal string? ProfileSelectedModel(string id) => FindAdditional(id)?.SelectedModelId;

    internal IReadOnlyList<PluginModelInfo> ProfileTranscriptionModels(string id)
    {
        var profile = FindAdditional(id);
        if (profile is null)
            return [];

        var models = profile.FetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();
        if (models.Count == 0 && !string.IsNullOrWhiteSpace(profile.SelectedModelId))
            return [new PluginModelInfo(profile.SelectedModelId!, profile.SelectedModelId!)];

        return models;
    }

    internal IReadOnlyList<PluginModelInfo> ProfileLlmModels(string id)
    {
        var profile = FindAdditional(id);
        if (profile is null)
            return [];

        var models = profile.FetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();
        if (models.Count == 0 && !string.IsNullOrWhiteSpace(profile.SelectedLlmModelId))
            return [new PluginModelInfo(profile.SelectedLlmModelId!, profile.SelectedLlmModelId!)];

        return models;
    }

    internal void SelectProfileModel(string id, string modelId)
    {
        var profile = FindAdditional(id);
        if (profile is null)
            return;

        var selectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        if (string.Equals(profile.SelectedModelId, selectedModelId, StringComparison.Ordinal))
            return;

        profile.SelectedModelId = selectedModelId;
        lock (_profileRolesLock)
        {
            _profileRoles.Remove(id);
        }

        PersistAdditionalProfiles(notify: false);
    }

    internal async Task<PluginTranscriptionResult> TranscribeForProfileAsync(
        string id,
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        var profile = RequireAdditional(id);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));
        if (string.IsNullOrEmpty(profile.SelectedModelId))
            throw new InvalidOperationException(Loc.L("Settings.NoTranscriptionModelSelected"));

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            profile.BaseUrl,
            GetProfileApiKey(id) ?? "",
            profile.SelectedModelId!,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt
        );
    }

    internal async Task<string> ProcessForProfileAsync(
        string id,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        var profile = RequireAdditional(id);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException(Loc.L("Settings.NoLlmModelSelected"));

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            profile.BaseUrl,
            GetProfileApiKey(id) ?? "",
            modelId,
            systemPrompt,
            userText,
            ct
        );
    }

    // Mirrors the default endpoint's streaming behavior for an additional profile:
    // honors the shared streamResponses toggle and streams token deltas via the
    // OpenAI chat helper, so prompt actions through a profile don't silently
    // regress to a single bulk chunk.
    internal async IAsyncEnumerable<string> ProcessStreamingForProfileAsync(
        string id,
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!_streamResponses)
        {
            yield return await ProcessForProfileAsync(id, systemPrompt, userText, model, ct);
            yield break;
        }

        var profile = RequireAdditional(id);
        if (string.IsNullOrEmpty(profile.BaseUrl))
            throw new InvalidOperationException(Loc.L("Settings.ServerUrlNotConfigured"));

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException(Loc.L("Settings.NoLlmModelSelected"));

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            profile.BaseUrl,
            GetProfileApiKey(id) ?? "",
            modelId,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    private async Task LoadAdditionalProfilesAsync(IPluginHostServices host)
    {
        var previousById = _additionalProfiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var previousApiKeys = new Dictionary<string, string?>(_additionalApiKeys, StringComparer.Ordinal);
        _additionalProfiles.Clear();
        _additionalApiKeys.Clear();

        // Nullable elements deliberately: the persisted JSON is user-editable and the deserializer
        // ignores the declared types, so nulls reach us — and an NRE here fails activation with no
        // way back through the UI.
        var stored = host.GetSetting<List<OpenAiCompatibleProfile?>>(AdditionalProfilesSettingKey) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- hoisting the null guard into a Where() switches enumerators and loses the compiler's null-narrowing inside the body, which then needs `!` on every access.
        foreach (var profile in stored)
        {
            if (profile is null)
                continue;

            profile.Id = NormalizeProfileId(profile.Id, seen);

            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Custom Server" : profile.Name.Trim();
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- the annotation states the C# contract; the deserializer that produced this value ignores it.
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl ?? "");

            profile.SelectedModelId = NullIfWhiteSpace(profile.SelectedModelId);
            profile.SelectedLlmModelId = NullIfWhiteSpace(profile.SelectedLlmModelId);

            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- same reason as BaseUrl above.
            IEnumerable<FetchedModel?> fetched = profile.FetchedModels ?? [];
            profile.FetchedModels = fetched
                .Where(m => !string.IsNullOrWhiteSpace(m?.Id))
                .Select(m => m!)
                .ToList();

            _additionalProfiles.Add(profile);

            var key = await host.LoadSecretAsync(SecretKeyFor(profile.Id));
            if (!string.IsNullOrEmpty(key))
                _additionalApiKeys[profile.Id] = key;
        }

        var changedApiKeyIds = previousApiKeys
            .Keys.Union(_additionalApiKeys.Keys, StringComparer.Ordinal)
            .Where(id =>
                !string.Equals(
                    previousApiKeys.GetValueOrDefault(id),
                    _additionalApiKeys.GetValueOrDefault(id),
                    StringComparison.Ordinal
                )
            );
        InvalidateChangedProfileRoles(previousById, changedApiKeyIds);
    }

    private void PersistAdditionalProfiles(bool notify)
    {
        _host?.SetSetting(AdditionalProfilesSettingKey, _additionalProfiles);
        if (notify)
            _host?.NotifyCapabilitiesChanged();
    }

    private async Task<List<FetchedModel>?> FetchModelsForAsync(
        string? baseUrl,
        string? apiKey,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(baseUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            return data.EnumerateArray()
                .Select(e => new FetchedModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null
                ))
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .OrderBy(m => m.Id)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? Get(PluginCollectionItem item, string key) =>
        item.Values.GetValueOrDefault(key);

    private static string SecretKeyFor(string profileId) => $"api-key.{profileId}";

    private string? GetProfileApiKey(string id) =>
        _additionalApiKeys.GetValueOrDefault(id);

    private OpenAiCompatibleProfileRole GetProfileRole(string id)
    {
        lock (_profileRolesLock)
        {
            // ReSharper disable once InvertIf -- standard get-or-add shape; inverting would duplicate the return.
            if (!_profileRoles.TryGetValue(id, out var role))
            {
                role = new OpenAiCompatibleProfileRole(this, id);
                _profileRoles.Add(id, role);
            }

            return role;
        }
    }

    private void InvalidateChangedProfileRoles(
        Dictionary<string, OpenAiCompatibleProfile> previousById,
        IEnumerable<string> changedSecretIds
    )
    {
        var changedSecrets = changedSecretIds.ToHashSet(StringComparer.Ordinal);
        var currentById = _additionalProfiles.ToDictionary(p => p.Id, StringComparer.Ordinal);

        lock (_profileRolesLock)
        {
            foreach (var id in _profileRoles.Keys.ToList())
            {
                if (
                    !previousById.TryGetValue(id, out var previous)
                    || !currentById.TryGetValue(id, out var current)
                    || changedSecrets.Contains(id)
                    || !ProfilesEqual(previous, current)
                )
                {
                    _profileRoles.Remove(id);
                }
            }
        }
    }

    private static bool ProfilesEqual(
        OpenAiCompatibleProfile left,
        OpenAiCompatibleProfile right
    )
    {
        return string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.Ordinal)
            && string.Equals(left.SelectedModelId, right.SelectedModelId, StringComparison.Ordinal)
            && string.Equals(
                left.SelectedLlmModelId,
                right.SelectedLlmModelId,
                StringComparison.Ordinal
            )
            && left.FetchedModels.SequenceEqual(right.FetchedModels);
    }

    private OpenAiCompatibleProfile? FindAdditional(string id) =>
        _additionalProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private OpenAiCompatibleProfile RequireAdditional(string id) =>
        FindAdditional(id) ?? throw new ArgumentException($"Unknown OpenAI-compatible profile: {id}", nameof(id));

    private static string NormalizeBaseUrl(string url)
    {
        var normalized = url.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];
        return normalized;
    }

    // Validates a profile id and adds it to taken. A trimmed id is kept only when
    // it is non-empty, colon-free (so it round-trips inside plugin:{id}:{model}),
    // carries the profile prefix, and is not already taken; otherwise a fresh id is
    // generated. Centralizes the SetItemsAsync and LoadAdditionalProfilesAsync sites
    // so repaired/normalized ids replace invalid or duplicate ones.
    private string NormalizeProfileId(string? rawId, HashSet<string> taken)
    {
        var id = (rawId ?? "").Trim();
        if (id.Length == 0
            || id.Contains(':')
            || !id.StartsWith(ProfileIdPrefix, StringComparison.Ordinal)
            || taken.Contains(id))
        {
            id = CreateProfileId(taken);
        }

        taken.Add(id);
        return id;
    }

    private string CreateProfileId(HashSet<string> taken)
    {
        string id;
        do
        {
            id = $"{ProfileIdPrefix}{Guid.NewGuid():N}";
        }
        while (taken.Contains(id)
            || _additionalProfiles.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)));

        return id;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Cached wrapper that presents one additional profile as a standalone
    // transcription engine / LLM provider. Its selection identity is the profile
    // ID; PluginId stays the owner's so host lookups (enable-state, settings)
    // still resolve to the real plugin. The owner is its only lifetime authority.
    private sealed class OpenAiCompatibleProfileRole(OpenAiCompatiblePlugin owner, string profileId)
        : ITranscriptionEngineRole,
            ILlmProviderRole,
            ITranscriptionEngineSelectionIdentity,
            ILlmProviderSelectionIdentity
    {
        public string PluginId => owner.PluginId;
        public string TranscriptionSelectionId => profileId;
        public string LlmSelectionId => profileId;
        public string ProviderId => profileId;
        public string ProviderDisplayName => owner.ProfileDisplayName(profileId);
        public bool IsConfigured => owner.ProfileConfigured(profileId);
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => owner.ProfileTranscriptionModels(profileId);
        public string? SelectedModelId => owner.ProfileSelectedModel(profileId);
        public bool SupportsTranslation => true;
        public string ProviderName => owner.ProfileDisplayName(profileId);
        public bool IsAvailable => owner.ProfileLlmAvailable(profileId);
        public IReadOnlyList<PluginModelInfo> SupportedModels => owner.ProfileLlmModels(profileId);

        public void SelectModel(string modelId) => owner.SelectProfileModel(profileId, modelId);

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        ) => owner.TranscribeForProfileAsync(profileId, wavAudio, language, translate, prompt, ct);

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        ) => owner.ProcessForProfileAsync(profileId, systemPrompt, userText, model, ct);

        public IAsyncEnumerable<string> ProcessStreamingAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        ) => owner.ProcessStreamingForProfileAsync(profileId, systemPrompt, userText, model, ct);

    }
}

/// <summary>Persisted OpenAI-compatible provider profile. API keys are stored separately as secrets.</summary>
public sealed class OpenAiCompatibleProfile
{
    /// <summary>Stable profile identifier (no colons, so it round-trips in plugin model IDs).</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable profile name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base server URL without a trailing /v1 suffix.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Optional default transcription model ID.</summary>
    public string? SelectedModelId { get; set; }

    /// <summary>Optional default LLM model ID.</summary>
    public string? SelectedLlmModelId { get; set; }

    /// <summary>Models fetched from the provider. API keys are never stored here.</summary>
    public List<FetchedModel> FetchedModels { get; set; } = [];

    /// <summary>Display name with a fallback for unnamed profiles.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Custom Server" : Name.Trim();
}

public sealed record FetchedModel(string Id, string? OwnedBy);
