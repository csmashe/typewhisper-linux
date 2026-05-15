using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configPath;

    public WebhookStore(string dataDir)
    {
        _configPath = Path.Combine(dataDir, "webhooks.json");
    }

    /// <summary>Loads stored configs; returns an empty list when missing or unreadable.</summary>
    public List<WebhookConfig> Load()
    {
        if (!File.Exists(_configPath)) return [];

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<List<WebhookConfig>>(json, s_jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Persists the supplied configs, creating the data directory if needed.</summary>
    public void Save(IEnumerable<WebhookConfig> configs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var json = JsonSerializer.Serialize(configs.ToList(), s_jsonOptions);
        File.WriteAllText(_configPath, json);
    }
}

public sealed class WebhookService
{
    private const int MaxLogEntries = 20;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient = new();
    private readonly IPluginHostServices _host;
    private readonly WebhookStore _store;

    public ObservableCollection<WebhookConfig> Webhooks { get; } = [];
    public ObservableCollection<DeliveryLogEntry> DeliveryLog { get; } = [];

    public WebhookService(IPluginHostServices host)
    {
        _host = host;
        _store = new WebhookStore(host.PluginDataDirectory);
        Load();
    }

    public void AddWebhook(WebhookConfig config)
    {
        Webhooks.Add(config);
        Save();
    }

    public void RemoveWebhook(Guid id)
    {
        var webhook = Webhooks.FirstOrDefault(w => w.Id == id);
        if (webhook is not null)
        {
            Webhooks.Remove(webhook);
            Save();
        }
    }

    public void UpdateWebhook(WebhookConfig updated)
    {
        for (var i = 0; i < Webhooks.Count; i++)
        {
            if (Webhooks[i].Id == updated.Id)
            {
                Webhooks[i] = updated;
                Save();
                return;
            }
        }
    }

    /// <summary>Replaces every stored webhook with the supplied set and persists.</summary>
    public void ReplaceAll(IEnumerable<WebhookConfig> configs)
    {
        Webhooks.Clear();
        foreach (var config in configs)
            Webhooks.Add(config);
        Save();
    }

    public async Task SendWebhooksAsync(TranscriptionCompletedEvent evt)
    {
        foreach (var webhook in Webhooks.ToList())
        {
            if (!webhook.IsEnabled) continue;

            if (webhook.ProfileFilter.Count > 0
                && (evt.ProfileName is null || !webhook.ProfileFilter.Contains(evt.ProfileName)))
                continue;

            await SendSingleAsync(webhook, evt, retryOnFailure: true);
        }
    }

    private async Task SendSingleAsync(WebhookConfig webhook, TranscriptionCompletedEvent evt, bool retryOnFailure)
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
                timestamp = evt.Timestamp
            };

            var json = JsonSerializer.Serialize(payload, s_jsonOptions);
            var method = webhook.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                ? System.Net.Http.HttpMethod.Put
                : System.Net.Http.HttpMethod.Post;

            using var request = new HttpRequestMessage(method, webhook.Url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            foreach (var header in webhook.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var response = await _httpClient.SendAsync(request);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                AddLogEntry(new DeliveryLogEntry
                {
                    WebhookName = webhook.Name,
                    Url = webhook.Url,
                    StatusCode = statusCode,
                    Success = true
                });
            }
            else
            {
                AddLogEntry(new DeliveryLogEntry
                {
                    WebhookName = webhook.Name,
                    Url = webhook.Url,
                    StatusCode = statusCode,
                    Error = $"HTTP {statusCode}",
                    Success = false
                });

                if (retryOnFailure)
                {
                    await Task.Delay(5000);
                    await SendSingleAsync(webhook, evt, retryOnFailure: false);
                }
            }
        }
        catch (Exception ex)
        {
            AddLogEntry(new DeliveryLogEntry
            {
                WebhookName = webhook.Name,
                Url = webhook.Url,
                Error = ex.Message,
                Success = false
            });

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
        try
        {
            foreach (var config in _store.Load())
                Webhooks.Add(config);
        }
        catch
        {
            _host.Log(PluginLogLevel.Warning, "Failed to load webhook configuration");
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(Webhooks);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to save webhook configuration: {ex.Message}");
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class WebhookPlugin : ITypeWhisperPlugin, IPluginCollectionSettingsProvider, IPluginDataLocationAware
{
    private IDisposable? _subscription;
    private IPluginHostServices? _host;
    private string? _dataDirectory;

    public string PluginId => "com.typewhisper.webhook";
    public string PluginName => "Webhook";
    public string PluginVersion => "2.0.0";

    public WebhookService? Service { get; private set; }

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Service = new WebhookService(host);
        _subscription = host.EventBus.Subscribe<TranscriptionCompletedEvent>(OnTranscriptionCompleted);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public IPluginHostServices? Host => _host;

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent evt)
        => Service?.SendWebhooksAsync(evt) ?? Task.CompletedTask;

    public void Dispose()
    {
        _subscription?.Dispose();
        Service?.Dispose();
    }

    // --- IPluginDataLocationAware ---------------------------------------

    public void SetDataDirectory(string pluginDataDirectory) => _dataDirectory = pluginDataDirectory;

    private string ResolveDataDir()
        => _dataDirectory
           ?? throw new InvalidOperationException("Webhook plugin data directory has not been set.");

    // --- IPluginCollectionSettingsProvider ------------------------------

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
    [
        new PluginCollectionDefinition(
            Key: "webhooks",
            Label: "Webhooks",
            Description: "HTTP endpoints notified when a transcription completes.",
            ItemFields:
            [
                new PluginSettingDefinition("name", "Name", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("url", "URL",
                    Placeholder: "https://example.com/hook", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("method", "Method",
                    Options: [new PluginSettingOption("POST", "POST"), new PluginSettingOption("PUT", "PUT")],
                    Kind: PluginSettingKind.Dropdown),
                new PluginSettingDefinition("headers", "Headers",
                    Description: "One Name: Value per line.", Kind: PluginSettingKind.Multiline),
                new PluginSettingDefinition("profiles", "Profile filter",
                    Description: "One profile name per line; blank = all profiles.",
                    Kind: PluginSettingKind.Multiline),
                new PluginSettingDefinition("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
                new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text)
            ],
            ItemLabelFieldKey: "name",
            AddButtonLabel: "Add webhook")
    ];

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default)
    {
        if (collectionKey != "webhooks")
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);

        var source = Service?.Webhooks.AsEnumerable() ?? new WebhookStore(ResolveDataDir()).Load();

        IReadOnlyList<PluginCollectionItem> items = source
            .Select(c => new PluginCollectionItem(new Dictionary<string, string?>
            {
                ["name"] = c.Name,
                ["url"] = c.Url,
                ["method"] = c.HttpMethod,
                ["headers"] = SerializeHeaders(c.Headers),
                ["profiles"] = SerializeProfiles(c.ProfileFilter),
                ["enabled"] = c.IsEnabled ? "true" : "false",
                ["__id"] = c.Id.ToString("D")
            }))
            .ToList();

        return Task.FromResult(items);
    }

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey, IReadOnlyList<PluginCollectionItem> items, CancellationToken ct = default)
    {
        if (collectionKey != "webhooks")
            return Task.FromResult(new PluginSettingsValidationResult(false, "Unknown collection."));

        var configs = new List<WebhookConfig>(items.Count);

        foreach (var item in items)
        {
            var name = (Get(item, "name") ?? "").Trim();
            var label = name.Length == 0 ? "(unnamed)" : name;

            if (name.Length == 0)
                return Fail(label, "name is required.");

            var url = (Get(item, "url") ?? "").Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Fail(label, "URL must start with http:// or https://.");

            var rawMethod = (Get(item, "method") ?? "").Trim();
            if (!rawMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && !rawMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                return Fail(label, "method must be POST or PUT.");
            var method = rawMethod.ToUpperInvariant();

            var headersText = Get(item, "headers") ?? "";
            if (!TryParseHeaders(headersText, out var headers, out var headerError))
                return Fail(label, headerError);

            var enabled = !TryGetBool(item, "enabled", out var parsed) || parsed;

            var id = Guid.TryParse(Get(item, "__id"), out var parsedId) ? parsedId : Guid.NewGuid();

            configs.Add(new WebhookConfig
            {
                Id = id,
                Name = name,
                Url = url,
                HttpMethod = method,
                Headers = headers,
                ProfileFilter = ParseProfiles(Get(item, "profiles") ?? ""),
                IsEnabled = enabled
            });
        }

        if (Service is not null)
            Service.ReplaceAll(configs);
        else
            new WebhookStore(ResolveDataDir()).Save(configs);

        return Task.FromResult(new PluginSettingsValidationResult(true, "Saved."));

        static Task<PluginSettingsValidationResult> Fail(string label, string reason)
            => Task.FromResult(new PluginSettingsValidationResult(false, $"Webhook '{label}': {reason}"));
    }

    private static string? Get(PluginCollectionItem item, string key)
        => item.Values.TryGetValue(key, out var value) ? value : null;

    private static bool TryGetBool(PluginCollectionItem item, string key, out bool value)
    {
        var raw = Get(item, key);
        if (raw is not null && bool.TryParse(raw, out value))
            return true;
        value = false;
        return false;
    }

    // --- Header serialization helpers -----------------------------------

    /// <summary>Serializes headers to one <c>Name: Value</c> line each.</summary>
    internal static string SerializeHeaders(IReadOnlyDictionary<string, string> headers)
        => string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"));

    /// <summary>
    /// Parses multiline header text. Each non-blank line is split on the first
    /// <c>:</c> only. Returns false with an error message when a line is malformed.
    /// </summary>
    internal static bool TryParseHeaders(string? text, out Dictionary<string, string> headers, out string error)
    {
        headers = [];
        error = "";

        if (string.IsNullOrWhiteSpace(text))
            return true;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                error = $"header line '{line}' is missing a ':' separator.";
                return false;
            }

            var key = line[..colon].Trim();
            if (key.Length == 0)
            {
                error = $"header line '{line}' has an empty name.";
                return false;
            }

            headers[key] = line[(colon + 1)..].Trim();
        }

        return true;
    }

    // --- Profile filter serialization helpers ---------------------------

    /// <summary>Serializes the profile filter to one profile name per line.</summary>
    internal static string SerializeProfiles(IEnumerable<string> profiles)
        => string.Join("\n", profiles);

    /// <summary>Parses multiline profile text; trims each entry and skips blank lines.</summary>
    internal static List<string> ParseProfiles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }
}
