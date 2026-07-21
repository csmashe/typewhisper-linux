// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Webhook;

public sealed record WebhookConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string HttpMethod { get; init; } = "POST";
    public Dictionary<string, string> Headers { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
    // ReSharper disable once TypeWithSuspiciousEqualityIsUsedInRecord.Global -- config record identity is its Id; the collection members are never compared by value.
    public List<string> ProfileFilter { get; init; } = [];
}

public sealed record DeliveryLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string WebhookName { get; init; } = "";
    public string Url { get; init; } = "";
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// Host-independent persistence for webhook configurations. Reads and writes
/// <c>webhooks.json</c> in the supplied data directory.
/// </summary>
internal sealed class WebhookStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configPath;

    public WebhookStore(string dataDir)
    {
        _configPath = Path.Join(dataDir, "webhooks.json");
    }

    /// <summary>
    /// Loads stored configs; returns an empty list only when the file does not
    /// exist. Read or JSON-parse failures propagate so the caller can log them
    /// rather than mistaking a corrupt file for "no webhooks" and overwriting it.
    /// </summary>
    public List<WebhookConfig> Load()
    {
        if (!File.Exists(_configPath))
            return [];

        var json = File.ReadAllText(_configPath);
        return JsonSerializer.Deserialize<List<WebhookConfig>>(json, s_jsonOptions) ?? [];
    }

    /// <summary>
    /// Persists the supplied configs, creating the data directory if needed.
    /// Writes through a sibling temp file and renames it over the target so a
    /// crash or kill mid-write can't truncate webhooks.json.
    /// </summary>
    public void Save(IEnumerable<WebhookConfig> configs)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(configs.ToList(), s_jsonOptions);
        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);

            if (File.Exists(_configPath))
            {
                File.Replace(tempPath, _configPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _configPath);
            }

            tempPath = null!;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // best effort cleanup
                }
            }
        }
    }
}

public sealed class WebhookService
{
    private const int MaxLogEntries = 20;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient = new();
    private readonly IPluginHostServices _host;
    private readonly WebhookStore _store;
    // Guards every mutation and enumeration of Webhooks so the
    // ObservableCollection isn't torn between the UI / settings-save thread
    // and the EventBus delivery thread. SendWebhooksAsync only holds this
    // lock briefly to take a snapshot, so a slow disk write inside Save()
    // can't stall webhook deliveries.
    private readonly Lock _webhooksLock = new();
    // Serializes the mutate-then-persist sequence so two overlapping saves
    // can't reorder writes — without this, thread A could snapshot first,
    // thread B could snapshot (including A's mutation) and write first, then
    // thread A would write its older snapshot last and clobber B's state on
    // disk while memory still reflects B's mutation.
    private readonly Lock _saveLock = new();
    private bool _loadSucceeded;

    public ObservableCollection<WebhookConfig> Webhooks { get; } = [];
    public ObservableCollection<DeliveryLogEntry> DeliveryLog { get; } = [];

    public WebhookService(IPluginHostServices host, string dataDirectory)
    {
        _host = host;
        _store = new WebhookStore(dataDirectory);
        Load();
    }

    public void AddWebhook(WebhookConfig config)
    {
        // _saveLock spans mutation + snapshot + Save so concurrent mutations
        // can't reorder their writes on disk. SendWebhooksAsync only takes
        // _webhooksLock briefly for its snapshot, so it isn't blocked by the
        // disk write held under _saveLock.
        lock (_saveLock)
        {
            List<WebhookConfig> snapshot;
            lock (_webhooksLock)
            {
                Webhooks.Add(config);
                snapshot = Webhooks.ToList();
            }
            Save(snapshot);
        }
    }

    public void RemoveWebhook(Guid id)
    {
        lock (_saveLock)
        {
            List<WebhookConfig>? snapshot = null;
            lock (_webhooksLock)
            {
                var webhook = Webhooks.FirstOrDefault(w => w.Id == id);
                if (webhook is not null)
                {
                    Webhooks.Remove(webhook);
                    snapshot = Webhooks.ToList();
                }
            }

            if (snapshot is not null)
                Save(snapshot);
        }
    }

    public void UpdateWebhook(WebhookConfig updated)
    {
        lock (_saveLock)
        {
            List<WebhookConfig>? snapshot = null;
            lock (_webhooksLock)
            {
                for (var i = 0; i < Webhooks.Count; i++)
                {
                    // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                    if (Webhooks[i].Id == updated.Id)
                    {
                        Webhooks[i] = updated;
                        snapshot = Webhooks.ToList();
                        break;
                    }
                }
            }

            if (snapshot is not null)
                Save(snapshot);
        }
    }

    /// <summary>Replaces every stored webhook with the supplied set and persists.</summary>
    public void ReplaceAll(IEnumerable<WebhookConfig> configs)
    {
        // _saveLock keeps the mutate-snapshot-Save sequence ordered against
        // other mutations so overlapping ReplaceAll/AddWebhook calls can't
        // land an older snapshot on disk after a newer one. Save() rethrows
        // on persistence failure; we snapshot the previous set first so we
        // can roll the ObservableCollection back to disk-truth if the write
        // fails.
        lock (_saveLock)
        {
            List<WebhookConfig> previous;
            List<WebhookConfig> snapshot;
            lock (_webhooksLock)
            {
                previous = Webhooks.ToList();
                Webhooks.Clear();
                foreach (var config in configs)
                    Webhooks.Add(config);
                snapshot = Webhooks.ToList();
            }

            try
            {
                Save(snapshot);
            }
            catch
            {
                lock (_webhooksLock)
                {
                    Webhooks.Clear();
                    foreach (var config in previous)
                        Webhooks.Add(config);
                }
                throw;
            }
        }
    }

    /// <summary>Returns a thread-safe snapshot of the current webhook set.</summary>
    public IReadOnlyList<WebhookConfig> SnapshotWebhooks()
    {
        lock (_webhooksLock)
            return Webhooks.ToList();
    }

    public async Task SendWebhooksAsync(TranscriptionCompletedEvent evt)
    {
        List<WebhookConfig> snapshot;
        lock (_webhooksLock)
            snapshot = Webhooks.ToList();

        foreach (var webhook in snapshot)
        {
            if (!webhook.IsEnabled)
                continue;

            if (
                webhook.ProfileFilter.Count > 0
                && (evt.ProfileName is null || !webhook.ProfileFilter.Contains(evt.ProfileName))
            )
                continue;

            await SendSingleAsync(webhook, evt, retryOnFailure: true);
        }
    }

    private async Task SendSingleAsync(
        WebhookConfig webhook,
        TranscriptionCompletedEvent evt,
        bool retryOnFailure
    )
    {
        try
        {
            var payload = new
            {
                text = evt.Text,
                detectedLanguage = evt.DetectedLanguage,
                durationSeconds = evt.DurationSeconds,
                modelId = evt.ModelId,
                profileName = evt.ProfileName,
                timestamp = evt.Timestamp,
            };

            var json = JsonSerializer.Serialize(payload, s_jsonOptions);
            var method = webhook.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Put
                : HttpMethod.Post;

            using var request = new HttpRequestMessage(method, webhook.Url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            foreach (var header in webhook.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            using var response = await _httpClient.SendAsync(request);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                AddLogEntry(
                    new DeliveryLogEntry
                    {
                        WebhookName = webhook.Name,
                        Url = webhook.Url,
                        StatusCode = statusCode,
                        Success = true,
                    }
                );
            }
            else
            {
                AddLogEntry(
                    new DeliveryLogEntry
                    {
                        WebhookName = webhook.Name,
                        Url = webhook.Url,
                        StatusCode = statusCode,
                        Error = $"HTTP {statusCode}",
                        Success = false,
                    }
                );

                if (retryOnFailure)
                {
                    await Task.Delay(5000);
                    await SendSingleAsync(webhook, evt, retryOnFailure: false);
                }
            }
        }
        catch (Exception ex)
        {
            AddLogEntry(
                new DeliveryLogEntry
                {
                    WebhookName = webhook.Name,
                    Url = webhook.Url,
                    Error = ex.Message,
                    Success = false,
                }
            );

            if (retryOnFailure)
            {
                await Task.Delay(5000);
                await SendSingleAsync(webhook, evt, retryOnFailure: false);
            }
        }
    }

    private void AddLogEntry(DeliveryLogEntry entry)
    {
        DeliveryLog.Insert(0, entry);
        while (DeliveryLog.Count > MaxLogEntries)
            DeliveryLog.RemoveAt(DeliveryLog.Count - 1);
    }

    private void Load()
    {
        List<WebhookConfig> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception ex)
        {
            // Surface as Warning + keep Webhooks empty, but leave
            // _loadSucceeded=false so Save() refuses to overwrite a file
            // that may still hold valid webhooks behind a parse error.
            _host.Log(
                PluginLogLevel.Warning,
                $"Failed to load webhook configuration: {ex.Message}"
            );
            return;
        }

        foreach (var config in loaded)
            Webhooks.Add(config);
        _loadSucceeded = true;
    }

    private void Save(IReadOnlyList<WebhookConfig> snapshot)
    {
        if (!_loadSucceeded)
        {
            // Mirrors ScriptService: refuse to write when the existing
            // file failed to load, so a corrupt or locked webhooks.json
            // can't be silently replaced with an empty in-memory state.
            _host.Log(
                PluginLogLevel.Warning,
                "Refusing to save webhook configuration because the existing file could not be loaded."
            );
            throw new InvalidOperationException(
                "Cannot save webhook configuration because the existing file could not be loaded."
            );
        }

        try
        {
            _store.Save(snapshot);
        }
        catch (Exception ex)
        {
            _host.Log(
                PluginLogLevel.Warning,
                $"Failed to save webhook configuration: {ex.Message}"
            );
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class WebhookPlugin
    : ITypeWhisperPlugin,
        IPluginCollectionSettingsProvider,
        IPluginDataLocationAware,
        IPluginLocalizationAware
{
    private IDisposable? _subscription;
    private string? _dataDirectory;

    public string PluginId => "com.typewhisper.webhook";
    public string PluginName => "Webhook";
    public string PluginVersion => "2.0.0";

    public WebhookService? Service { get; private set; }

    public Task ActivateAsync(IPluginHostServices host)
    {
        Host = host;
        // Single canonical data dir: prefer the one set via SetDataDirectory
        // (called by the loader before ActivateAsync); fall back to the host's
        // value for hosts that don't drive IPluginDataLocationAware. Threading
        // the same string through WebhookService and ResolveDataDir() keeps
        // the live service and any on-disk fallback path reading/writing the
        // same webhooks.json.
        _dataDirectory ??= host.PluginDataDirectory;
        Service = new WebhookService(host, _dataDirectory);
        _subscription = host.EventBus.Subscribe<TranscriptionCompletedEvent>(
            OnTranscriptionCompleted
        );
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        // Dispose the service so its HttpClient is released; otherwise a
        // reactivation would observe stale state and leak the previous client.
        Service?.Dispose();
        Service = null;
        return Task.CompletedTask;
    }

    public IPluginHostServices? Host { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => Host?.Localization ?? _injectedLocalization;

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent evt) =>
        Service?.SendWebhooksAsync(evt) ?? Task.CompletedTask;

    public void Dispose()
    {
        _subscription?.Dispose();
        Service?.Dispose();
    }

    public void SetDataDirectory(string pluginDataDirectory) =>
        _dataDirectory = pluginDataDirectory;

    private string ResolveDataDir() =>
        _dataDirectory
        ?? throw new InvalidOperationException("Webhook plugin data directory has not been set.");

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
        [
            new(
                Key: "webhooks",
                Label: Loc.L("Settings.Webhooks"),
                Description: Loc.L("Settings.WebhooksDescription"),
                ItemFields:
                [
                    new PluginSettingDefinition("name", Loc.L("Settings.Name"), Kind: PluginSettingKind.Text),
                    new PluginSettingDefinition(
                        "url",
                        Loc.L("Settings.Url"),
                        Placeholder: "https://example.com/hook",
                        Kind: PluginSettingKind.Text
                    ),
                    new PluginSettingDefinition(
                        "method",
                        Loc.L("Settings.Method"),
                        Options:
                        [
                            new PluginSettingOption("POST", "POST"),
                            new PluginSettingOption("PUT", "PUT"),
                        ],
                        Kind: PluginSettingKind.Dropdown
                    ),
                    new PluginSettingDefinition(
                        "headers",
                        Loc.L("Settings.Headers"),
                        Description: Loc.L("Settings.HeadersDescription"),
                        Kind: PluginSettingKind.Multiline
                    ),
                    new PluginSettingDefinition(
                        "profiles",
                        Loc.L("Settings.ProfileFilter"),
                        Description: Loc.L("Settings.ProfileFilterDescription"),
                        Kind: PluginSettingKind.Multiline
                    ),
                    new PluginSettingDefinition(
                        "enabled",
                        Loc.L("Settings.Enabled"),
                        Kind: PluginSettingKind.Boolean
                    ),
                    new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
                ],
                ItemLabelFieldKey: "name",
                AddButtonLabel: Loc.L("Settings.AddWebhook")
            ),
        ];

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    )
    {
        if (collectionKey != "webhooks")
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);

        // When the plugin hasn't been activated yet we fall back to loading
        // straight from disk. The store's Load propagates I/O / JSON errors —
        // catch them here so settings retrieval doesn't break on a corrupt file.
        IEnumerable<WebhookConfig> source;
        if (Service is not null)
        {
            source = Service.SnapshotWebhooks();
        }
        else
        {
            try
            {
                source = new WebhookStore(ResolveDataDir()).Load();
            }
            catch (Exception ex)
            {
                Host?.Log(PluginLogLevel.Warning, $"Failed to load webhooks: {ex.Message}");
                source = [];
            }
        }

        IReadOnlyList<PluginCollectionItem> items = source
            .Select(c => new PluginCollectionItem(
                new Dictionary<string, string?>
                {
                    ["name"] = c.Name,
                    ["url"] = c.Url,
                    ["method"] = c.HttpMethod,
                    ["headers"] = SerializeHeaders(c.Headers),
                    ["profiles"] = SerializeProfiles(c.ProfileFilter),
                    ["enabled"] = c.IsEnabled ? "true" : "false",
                    ["__id"] = c.Id.ToString("D"),
                }
            ))
            .ToList();

        return Task.FromResult(items);
    }

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    )
    {
        if (collectionKey != "webhooks")
            return Task.FromResult(
                new PluginSettingsValidationResult(false, Loc.L("Settings.UnknownCollection"))
            );

        var configs = new List<WebhookConfig>(items.Count);

        foreach (var item in items)
        {
            var name = (Get(item, "name") ?? "").Trim();
            var label = name.Length == 0 ? Loc.L("Settings.Unnamed") : name;

            if (name.Length == 0)
                return Fail(label, Loc.L("Settings.NameRequired"));

            var url = (Get(item, "url") ?? "").Trim();
            if (
                !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
                || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(parsedUrl.Host)
            )
                return Fail(label, Loc.L("Settings.UrlInvalid"));

            var rawMethod = (Get(item, "method") ?? "").Trim();
            if (
                !rawMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && !rawMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            )
                return Fail(label, Loc.L("Settings.MethodInvalid"));
            var method = rawMethod.ToUpperInvariant();

            var headersText = Get(item, "headers") ?? "";
            if (!TryParseHeaders(headersText, out var headers, out var headerError, Loc))
                return Fail(label, headerError);

            var enabled = !TryGetBool(item, "enabled", out var parsed) || parsed;

            var id = Guid.TryParse(Get(item, "__id"), out var parsedId) ? parsedId : Guid.NewGuid();

            configs.Add(
                new WebhookConfig
                {
                    Id = id,
                    Name = name,
                    Url = url,
                    HttpMethod = method,
                    Headers = headers,
                    ProfileFilter = ParseProfiles(Get(item, "profiles") ?? ""),
                    IsEnabled = enabled,
                }
            );
        }

        try
        {
            if (Service is not null)
                Service.ReplaceAll(configs);
            else
                new WebhookStore(ResolveDataDir()).Save(configs);
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new PluginSettingsValidationResult(
                    false,
                    Loc.L("Settings.FailedToSaveSettings", ex.Message)
                )
            );
        }

        return Task.FromResult(new PluginSettingsValidationResult(true, Loc.L("Settings.Saved")));

        Task<PluginSettingsValidationResult> Fail(string label, string reason) =>
            Task.FromResult(
                new PluginSettingsValidationResult(false, Loc.L("Settings.WebhookLabelReason", label, reason))
            );
    }

    private static string? Get(PluginCollectionItem item, string key) =>
        item.Values.TryGetValue(key, out var value) ? value : null;

    private static bool TryGetBool(PluginCollectionItem item, string key, out bool value)
    {
        var raw = Get(item, key);
        if (raw is not null && bool.TryParse(raw, out value))
            return true;
        value = false;
        return false;
    }

    /// <summary>Serializes headers to one <c>Name: Value</c> line each.</summary>
    internal static string SerializeHeaders(IReadOnlyDictionary<string, string> headers) =>
        string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"));

    /// <summary>
    /// Parses multiline header text. Each non-blank line is split on the first
    /// <c>:</c> only. Returns false with an error message when a line is malformed.
    /// </summary>
    internal static bool TryParseHeaders(
        string? text,
        out Dictionary<string, string> headers,
        out string error,
        IPluginLocalization? loc = null
    )
    {
        headers = [];
        error = "";

        if (string.IsNullOrWhiteSpace(text))
            return true;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                error = loc.L("Settings.HeaderMissingSeparator", line);
                return false;
            }

            var key = line[..colon].Trim();
            if (key.Length == 0)
            {
                error = loc.L("Settings.HeaderEmptyName", line);
                return false;
            }

            headers[key] = line[(colon + 1)..].Trim();
        }

        return true;
    }

    /// <summary>Serializes the profile filter to one profile name per line.</summary>
    internal static string SerializeProfiles(IEnumerable<string> profiles) =>
        string.Join("\n", profiles);

    /// <summary>Parses multiline profile text; trims each entry and skips blank lines.</summary>
    internal static List<string> ParseProfiles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}
