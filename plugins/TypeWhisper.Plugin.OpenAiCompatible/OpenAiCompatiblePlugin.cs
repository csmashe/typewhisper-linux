using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
    : ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        IPluginSettingsProvider,
        IModelCatalogProvider,
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
    private string? _apiKey;
    private string? _baseUrl;
    private string? _selectedModelId;
    private string? _selectedLlmModelId;
    private List<FetchedModel> _fetchedModels = [];
    private bool _streamResponses = true;
    private readonly List<OpenAiCompatibleProfile> _additionalProfiles = [];
    private readonly Dictionary<string, string?> _additionalApiKeys = new(StringComparer.Ordinal);

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
        _apiKey = await host.LoadSecretAsync("api-key");
        _baseUrl = host.GetSetting<string>("baseUrl");
        _selectedModelId = host.GetSetting<string>("selectedModel");
        _selectedLlmModelId = host.GetSetting<string>("selectedLlmModel");
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

        host.Log(
            PluginLogLevel.Info,
            $"Activated (baseUrl={_baseUrl}, configured={IsConfigured}, "
                + $"additionalProfiles={_additionalProfiles.Count})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "openai-compatible";
    public string ProviderDisplayName => "Custom Server";

    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels
    {
        get
        {
            var models = _fetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(_selectedModelId))
                return [new PluginModelInfo(_selectedModelId, _selectedModelId)];

            return models;
        }
    }

    public string? SelectedModelId => _selectedModelId;

    public void SelectModel(string modelId)
    {
        _selectedModelId = modelId;
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
        if (string.IsNullOrEmpty(_baseUrl))
            throw new InvalidOperationException("Server-URL nicht konfiguriert");
        if (string.IsNullOrEmpty(_selectedModelId))
            throw new InvalidOperationException("Kein Transkriptions-Modell ausgewählt");

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            _baseUrl!,
            _apiKey ?? "",
            _selectedModelId!,
            wavAudio,
            language,
            translate,
            "verbose_json",
            ct,
            prompt
        );
    }

    public string ProviderName => "OpenAI Compatible";

    public bool IsAvailable => IsConfigured && !string.IsNullOrEmpty(_selectedLlmModelId);

    public IReadOnlyList<PluginModelInfo> SupportedModels
    {
        get
        {
            var models = _fetchedModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList();

            if (models.Count == 0 && !string.IsNullOrEmpty(_selectedLlmModelId))
                return [new PluginModelInfo(_selectedLlmModelId, _selectedLlmModelId)];

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
        if (string.IsNullOrEmpty(_baseUrl))
            throw new InvalidOperationException("Server-URL nicht konfiguriert");

        var modelId = !string.IsNullOrEmpty(model) ? model : _selectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("Kein LLM-Modell ausgewählt");

        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            _baseUrl!,
            _apiKey ?? "",
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

        if (string.IsNullOrEmpty(_baseUrl))
            throw new InvalidOperationException("Server-URL nicht konfiguriert");

        var modelId = !string.IsNullOrEmpty(model) ? model : _selectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("Kein LLM-Modell ausgewählt");

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            _baseUrl!,
            _apiKey ?? "",
            modelId,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
    }

    internal string? BaseUrl => _baseUrl;
    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedTranscriptionModelId => _selectedModelId;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<FetchedModel> FetchedModels => _fetchedModels;

    internal void SetBaseUrl(string url)
    {
        // Helpers append "/v1/..." themselves; pasted URLs often already
        // include "/v1", so strip a trailing "/v1" segment to avoid building
        // "/v1/v1/models" and similar paths.
        var normalized = url.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];
        _baseUrl = normalized;
        _host?.SetSetting("baseUrl", normalized);
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task SetApiKeyAsync(string key)
    {
        _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(key))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", key);

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        _selectedLlmModelId = modelId;
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
        _fetchedModels = models;
        try
        {
            var json = JsonSerializer.Serialize(models);
            _host?.SetSetting("fetchedModels", json);
        }
        catch
        { /* best effort */
        }
        if (notifyCapabilitiesChanged)
            _host?.NotifyCapabilitiesChanged();
    }

    // Returns null on a fetch/parse failure (so callers can keep their cached
    // list), and a (possibly empty) list on a successful /v1/models response —
    // an empty list is a valid "this server has zero models" answer, distinct
    // from "couldn't reach/parse the server."
    internal async Task<List<FetchedModel>?> FetchModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_baseUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

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
        if (string.IsNullOrEmpty(_baseUrl))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

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
                "Base URL",
                false,
                "http://localhost:8000",
                "OpenAI-compatible server base URL."
            ),
            new(
                "api-key",
                "API key",
                true,
                null,
                "Optional bearer token used when calling the server."
            ),
            new(
                "selectedModel",
                "Transcription model",
                Description: _fetchedModels.Count > 0
                    ? $"Showing {_fetchedModels.Count} fetched model(s)."
                    : "Click Validate after saving the server settings to fetch available models.",
                Options: BuildModelOptions()
            ),
            new(
                "selectedLlmModel",
                "LLM model",
                Description: _fetchedModels.Count > 0
                    ? $"Showing {_fetchedModels.Count} fetched model(s)."
                    : "Click Validate after saving the server settings to fetch available models.",
                Options: BuildModelOptions()
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: "Stream responses",
                Description: "Render prompt-action output token-by-token as the "
                    + "server generates it (works with Ollama, LM Studio, vLLM, etc.), "
                    + "instead of waiting for the full reply.",
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "baseUrl" => _baseUrl,
                "api-key" => _apiKey,
                "selectedModel" => _selectedModelId,
                "selectedLlmModel" => _selectedLlmModelId,
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

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return new PluginSettingsValidationResult(false, "Enter a base URL first.");

        var valid = await ValidateConnectionAsync(ct);
        if (!valid)
            return new PluginSettingsValidationResult(false, "Could not connect to the server.");

        var models = await FetchModelsAsync(ct) ?? [];
        SetFetchedModels(models, notifyCapabilitiesChanged: false);

        if (string.IsNullOrWhiteSpace(_selectedModelId) && models.Count > 0)
            SelectModel(models[0].Id);
        if (string.IsNullOrWhiteSpace(_selectedLlmModelId) && models.Count > 0)
            SelectLlmModel(models[0].Id);

        _host?.NotifyCapabilitiesChanged();

        return new PluginSettingsValidationResult(
            true,
            $"Connection OK. Fetched {models.Count} model(s)."
        );
    }

    // IModelCatalogProvider: read-only model-list refresh for dropdown-open.
    // Only the model catalog is touched — no connection-validation message, no
    // asset downloads, no auto-selecting a model. Keeps the cached list on a
    // transient failure (FetchModelsAsync returns null) so an unreachable
    // endpoint doesn't empty the dropdown, but honors a successful empty
    // response (empty list) so a server that legitimately dropped all its
    // models clears the cache.
    public async Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_baseUrl))
        {
            var models = await FetchModelsAsync(ct);
            if (models is not null && CatalogChanged(models, _fetchedModels))
                SetFetchedModels(models);
        }

        // Refresh additional profiles on the same dropdown-open path so their
        // catalogs don't go stale when a server adds or removes models after the
        // profile was first saved.
        var anyProfileChanged = false;
        foreach (var profile in _additionalProfiles.Where(p => !string.IsNullOrEmpty(p.BaseUrl)))
        {
            var models = await FetchModelsForAsync(profile.BaseUrl, GetProfileApiKey(profile.Id), ct);
            if (models is null || !CatalogChanged(models, profile.FetchedModels))
                continue;

            profile.FetchedModels = models;
            anyProfileChanged = true;
        }

        if (anyProfileChanged)
            PersistAdditionalProfiles(notify: true);
    }

    private static bool CatalogChanged(List<FetchedModel> fetched, IReadOnlyList<FetchedModel> current) =>
        fetched.Count != current.Count
        || !fetched.Select(m => m.Id).SequenceEqual(current.Select(m => m.Id));

    private IReadOnlyList<PluginSettingOption>? BuildModelOptions()
    {
        var models = _fetchedModels.Select(m => new PluginSettingOption(m.Id, m.Id)).ToList();

        if (models.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_selectedModelId))
                models.Add(new PluginSettingOption(_selectedModelId, _selectedModelId));
            if (
                !string.IsNullOrWhiteSpace(_selectedLlmModelId)
                && models.All(m => m.Value != _selectedLlmModelId)
            )
                models.Add(new PluginSettingOption(_selectedLlmModelId, _selectedLlmModelId));
        }

        return models.Count > 0 ? models : null;
    }

    // ---- Additional provider profiles ----------------------------------------
    // The default endpoint above is untouched. Everything below adds extra named
    // endpoints, each surfaced as its own selectable transcription engine / LLM
    // provider via OpenAiCompatibleProfileRole and the selection-identity scheme.

    public IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines =>
        _additionalProfiles
            .Select(p => (ITranscriptionEnginePlugin)new OpenAiCompatibleProfileRole(this, p.Id))
            .ToList();

    public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders =>
        _additionalProfiles
            .Select(p => (ILlmProviderPlugin)new OpenAiCompatibleProfileRole(this, p.Id))
            .ToList();

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
        [
            new PluginCollectionDefinition(
                Key: ProfilesCollectionKey,
                Label: "Additional provider profiles",
                Description: "Extra OpenAI-compatible endpoints. Each profile appears as its "
                    + "own transcription engine and LLM provider you can select in dictation "
                    + "and prompts. The default endpoint above is unaffected.",
                ItemFields:
                [
                    new PluginSettingDefinition(
                        "name", "Name", Placeholder: "Local Ollama", Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "baseUrl", "Base URL", Placeholder: "http://localhost:11434",
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "api-key", "API key", IsSecret: true,
                        Description: "Leave blank to keep the current key.",
                        Kind: PluginSettingKind.Secret),
                    new PluginSettingDefinition(
                        "selectedModel", "Transcription model",
                        Description: "Optional default; any fetched model can be chosen per workflow.",
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "selectedLlmModel", "LLM model",
                        Description: "Optional default; prompts can use any fetched model.",
                        Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
                ],
                ItemLabelFieldKey: "name",
                AddButtonLabel: "Add profile"
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
            return new PluginSettingsValidationResult(false, "Unknown collection.");

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

            var key = Get(item, "api-key");
            var keyChanged = !string.IsNullOrWhiteSpace(key);
            if (keyChanged)
                keyUpdates[id] = key!.Trim();

            // Preserve the fetched model catalog only when the endpoint is unchanged.
            // A changed base URL (or updated credentials) can point at a different
            // server, so drop the stale catalog and let the refetch below repopulate
            // it — otherwise the profile would keep advertising the previous server's
            // model IDs to dictation and prompt selection.
            var hadProfile = previousById.TryGetValue(id, out var prev);
            var endpointUnchanged = hadProfile
                && !keyChanged
                && string.Equals(prev!.BaseUrl, baseUrl, StringComparison.Ordinal);

            newProfiles.Add(new OpenAiCompatibleProfile
            {
                Id = id,
                Name = name.Length == 0 ? "Custom Server" : name,
                BaseUrl = baseUrl,
                SelectedModelId = NullIfWhiteSpace(Get(item, "selectedModel")),
                SelectedLlmModelId = NullIfWhiteSpace(Get(item, "selectedLlmModel")),
                FetchedModels = endpointUnchanged ? prev!.FetchedModels : [],
            });
        }

        // Do the fallible host secret-store writes before swapping the shared
        // profile set, so a failure here can't leave _additionalProfiles half
        // updated. Each _additionalApiKeys mutation is paired with its host op,
        // so the cache stays consistent with the store even on a mid-loop throw.
        if (_host is not null)
        {
            foreach (var removedId in previousById.Keys.Where(k => !seenIds.Contains(k)))
            {
                await _host.DeleteSecretAsync(SecretKeyFor(removedId));
                _additionalApiKeys.Remove(removedId);
            }

            foreach (var (id, key) in keyUpdates)
            {
                await _host.StoreSecretAsync(SecretKeyFor(id), key!);
                _additionalApiKeys[id] = key;
            }
        }

        _additionalProfiles.Clear();
        _additionalProfiles.AddRange(newProfiles);

        // State is now persisted; the best-effort model fetch below may fail or be
        // cancelled, but that must not revert the saved profiles.
        PersistAdditionalProfiles(notify: false);

        // Best-effort: populate model catalogs so prompts/dictation can list each
        // profile's models. New profiles and profiles whose endpoint changed have an
        // empty catalog here and get (re)fetched; an unchanged endpoint keeps its
        // existing catalog and is skipped.
        foreach (var profile in _additionalProfiles.Where(p => p.FetchedModels.Count == 0))
        {
            var models = await FetchModelsForAsync(profile.BaseUrl, GetProfileApiKey(profile.Id), ct);
            if (models is not null)
                profile.FetchedModels = models;
        }

        PersistAdditionalProfiles(notify: true);

        return new PluginSettingsValidationResult(true, $"Saved {_additionalProfiles.Count} profile(s).");
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

        profile.SelectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
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
            throw new InvalidOperationException("Server URL not configured.");
        if (string.IsNullOrEmpty(profile.SelectedModelId))
            throw new InvalidOperationException("No transcription model selected.");

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
            throw new InvalidOperationException("Server URL not configured.");

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("No LLM model selected.");

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
            throw new InvalidOperationException("Server URL not configured.");

        var modelId = !string.IsNullOrEmpty(model) ? model : profile.SelectedLlmModelId ?? "";
        if (string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("No LLM model selected.");

        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            profile.BaseUrl,
            GetProfileApiKey(id) ?? "",
            modelId,
            systemPrompt,
            userText,
            ct
        );

        await foreach (var delta in source.WithCancellation(ct))
            yield return delta;
    }

    private async Task LoadAdditionalProfilesAsync(IPluginHostServices host)
    {
        _additionalProfiles.Clear();
        _additionalApiKeys.Clear();

        var stored = host.GetSetting<List<OpenAiCompatibleProfile>>(AdditionalProfilesSettingKey) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var profile in stored.Where(p => p is not null))
        {
            profile.Id = NormalizeProfileId(profile.Id, seen);

            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Custom Server" : profile.Name.Trim();
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl ?? "");
            profile.SelectedModelId = NullIfWhiteSpace(profile.SelectedModelId);
            profile.SelectedLlmModelId = NullIfWhiteSpace(profile.SelectedLlmModelId);
            profile.FetchedModels = (profile.FetchedModels ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .ToList();

            _additionalProfiles.Add(profile);

            var key = await host.LoadSecretAsync(SecretKeyFor(profile.Id));
            if (!string.IsNullOrEmpty(key))
                _additionalApiKeys[profile.Id] = key;
        }
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
        item.Values.TryGetValue(key, out var value) ? value : null;

    private static string SecretKeyFor(string profileId) => $"api-key.{profileId}";

    private string? GetProfileApiKey(string id) =>
        _additionalApiKeys.TryGetValue(id, out var key) ? key : null;

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
    private string NormalizeProfileId(string? rawId, ISet<string> taken)
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

    private string CreateProfileId(ISet<string> taken)
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

    // Stateless wrapper that presents one additional profile as a standalone
    // transcription engine / LLM provider. Its selection identity is the profile
    // ID; PluginId stays the owner's so host lookups (enable-state, settings)
    // still resolve to the real plugin.
    private sealed class OpenAiCompatibleProfileRole(OpenAiCompatiblePlugin owner, string profileId)
        : ITranscriptionEnginePlugin,
            ILlmProviderPlugin,
            ITranscriptionEngineSelectionIdentity,
            ILlmProviderSelectionIdentity
    {
        public string PluginId => owner.PluginId;
        public string PluginName => owner.PluginName;
        public string PluginVersion => owner.PluginVersion;
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

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

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

        public void Dispose() { }
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
