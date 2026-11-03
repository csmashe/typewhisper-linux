// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
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
    public Dictionary<string, string> HeaderSecretReferences { get; init; } = [];
    [JsonIgnore]
    internal Dictionary<string, string> LegacyHeaders { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
    /// <summary>
    /// True delivers for every profile and for dictation without one; false
    /// delivers only for the names in <see cref="ProfileFilter"/>.
    /// </summary>
    public bool AllProfiles { get; init; } = true;
    // ReSharper disable once TypeWithSuspiciousEqualityIsUsedInRecord.Global -- config record identity is its Id; the collection members are never compared by value.
    public List<string> ProfileFilter { get; init; } = [];

    internal bool MatchesProfile(string? profileName) =>
        AllProfiles || (profileName is not null && ProfileFilter.Contains(profileName));
}

public enum DeliveryOutcome
{
    Delivered,
    HttpError,
    TimedOut,
    ConnectionFailed,
    HeaderSecretUnavailable,
    Failed,
}

/// <summary>
/// Carries no URL, header, payload or exception text, so the log cannot leak
/// a destination or a credential.
/// </summary>
public sealed record DeliveryLogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public string WebhookName { get; init; } = "";
    public DeliveryOutcome Outcome { get; init; }
    public int? StatusCode { get; init; }
    public bool IsTest { get; init; }
    public bool Success => Outcome == DeliveryOutcome.Delivered;
}

/// <summary>
/// Host-independent persistence for webhook configurations. Reads and writes
/// <c>webhooks.json</c> in the supplied data directory.
/// </summary>
internal sealed class WebhookStore
{
    // ReSharper disable AutoPropertyCanBeMadeGetOnly.Local -- init accessors are set by System.Text.Json deserialization via reflection, invisible to ReSharper's usage analysis.
    // ReSharper disable MemberCanBePrivate.Local -- properties must stay public for System.Text.Json to deserialize into them.
    // ReSharper disable once ClassNeverInstantiated.Local -- instantiated by System.Text.Json deserialization, which ReSharper cannot see.
    private sealed record StoredWebhookConfig
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; init; } = "";
        public string Url { get; init; } = "";
        public string HttpMethod { get; init; } = "POST";
        public Dictionary<string, string> HeaderSecretReferences { get; init; } = [];
        public Dictionary<string, string> Headers { get; init; } = [];
        public bool IsEnabled { get; init; } = true;
        // Absent in older files, where an empty filter meant every profile.
        // ReSharper disable once UnusedAutoPropertyAccessor.Local -- set by System.Text.Json deserialization via reflection.
        public bool? AllProfiles { get; init; }
        public List<string> ProfileFilter { get; init; } = [];

        public WebhookConfig ToConfig() =>
            new()
            {
                Id = Id,
                Name = Name,
                Url = Url,
                HttpMethod = HttpMethod,
                HeaderSecretReferences = new Dictionary<string, string>(
                    HeaderSecretReferences,
                    StringComparer.OrdinalIgnoreCase
                ),
                LegacyHeaders = new Dictionary<string, string>(
                    Headers,
                    StringComparer.OrdinalIgnoreCase
                ),
                IsEnabled = IsEnabled,
                AllProfiles = AllProfiles ?? ProfileFilter.Count == 0,
                ProfileFilter = ProfileFilter,
            };
    }
    // ReSharper restore MemberCanBePrivate.Local
    // ReSharper restore AutoPropertyCanBeMadeGetOnly.Local

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
    /// Legacy plaintext headers deserialize into <see cref="WebhookConfig.LegacyHeaders"/>.
    /// When <paramref name="protectExistingFile"/> is true, the file is set to
    /// 0600 before it is read.
    /// </summary>
    public List<WebhookConfig> Load(bool protectExistingFile = false)
    {
        if (!File.Exists(_configPath))
            return [];

        if (protectExistingFile && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _configPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
        }

        var json = File.ReadAllText(_configPath);
        return (
            JsonSerializer.Deserialize<List<StoredWebhookConfig>>(json, s_jsonOptions) ?? []
        ).Select(config => config.ToConfig()).ToList();
    }

    /// <summary>
    /// Persists the supplied configs, creating the data directory if needed.
    /// Writes through a sibling temp file and renames it over the target so a
    /// crash or kill mid-write can't truncate webhooks.json. The temp file is
    /// created with 0600 permissions before the rename.
    /// </summary>
    public void Save(IEnumerable<WebhookConfig> configs)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(configs.ToList(), s_jsonOptions);
        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

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

/// <summary>
/// Delivers completed transcriptions to the configured webhooks.
/// </summary>
/// <remarks>
/// Destinations are sent in parallel within one shared budget per event. A
/// destination is retried once, only when the request was not processed
/// (no connection, or HTTP 429/503); other failures may already have been acted
/// on, so they are not retried. An endpoint that answers 429/503 after acting
/// can still receive an event twice. Redirects are not followed, so header
/// secrets only reach the saved URL. Deactivation cancels in-flight deliveries.
/// </remarks>
public sealed class WebhookService : IDisposable
{
    private const int MaxLogEntries = 20;
    private static readonly TimeSpan s_defaultDeliveryBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IPluginHostServices _host;
    private readonly WebhookStore _store;
    private readonly TimeSpan _deliveryBudget;
    private readonly TimeSpan _retryDelay;
    // Not disposed: deliveries still running at Dispose read its token.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _logLock = new();
    private readonly List<DeliveryLogEntry> _deliveryLog = [];
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

    public WebhookService(IPluginHostServices host, string dataDirectory)
        : this(
            host,
            dataDirectory,
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
            s_defaultDeliveryBudget
        ) { }

    internal WebhookService(
        IPluginHostServices host,
        string dataDirectory,
        HttpMessageHandler handler,
        TimeSpan? deliveryBudget = null,
        TimeSpan? retryDelay = null
    )
    {
        _host = host;
        _store = new WebhookStore(dataDirectory);
        // Deadlines come from the delivery budget, not the client-wide timeout.
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _deliveryBudget = deliveryBudget ?? s_defaultDeliveryBudget;
        _retryDelay = retryDelay ?? s_defaultRetryDelay;
        Load(protectExistingFile: true);
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

    /// <summary>Returns the most recent deliveries, newest first.</summary>
    public IReadOnlyList<DeliveryLogEntry> SnapshotDeliveryLog()
    {
        lock (_logLock)
            return _deliveryLog.ToList();
    }

    public async Task SendWebhooksAsync(
        TranscriptionCompletedEvent evt,
        CancellationToken cancellationToken = default
    )
    {
        var targets = SnapshotWebhooks()
            .Where(webhook => webhook.IsEnabled && webhook.MatchesProfile(evt.ProfileName))
            .ToList();
        if (targets.Count == 0)
            return;

        var json = SerializePayload(
            evt.Text,
            evt.DetectedLanguage,
            evt.DurationSeconds,
            evt.ModelId,
            evt.ProfileName,
            evt.Timestamp
        );

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken
        );
        budget.CancelAfter(_deliveryBudget);

        await Task.WhenAll(
            targets.Select(webhook =>
                DeliverAsync(
                    webhook,
                    json,
                    // Re-checked before each attempt, so a retry skips a removed, disabled or redirected webhook.
                    current =>
                        current.IsEnabled
                        && current.MatchesProfile(evt.ProfileName)
                        && IsSameDestination(current, webhook),
                    maxAttempts: 2,
                    isTest: false,
                    // ReSharper disable once AccessToDisposedClosure -- the lambda runs inside Task.WhenAll, which is awaited before budget is disposed.
                    budget.Token,
                    cancellationToken
                )
            )
        );
    }

    /// <summary>
    /// Sends a sample message to the saved webhook, whether or not it is
    /// enabled or matches a profile. Nothing is persisted.
    /// </summary>
    public async Task<DeliveryLogEntry?> SendTestAsync(Guid webhookId, CancellationToken ct)
    {
        var webhook = SnapshotWebhooks().FirstOrDefault(candidate => candidate.Id == webhookId);
        if (webhook is null)
            return null;

        var json = SerializePayload(
            "This is a TypeWhisper test message.",
            "en",
            0,
            null,
            null,
            DateTimeOffset.UtcNow
        );

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        timeout.CancelAfter(s_testTimeout);

        return await DeliverAsync(
            webhook,
            json,
            current => IsSameDestination(current, webhook),
            maxAttempts: 1,
            isTest: true,
            timeout.Token,
            ct
        );
    }

    private static string SerializePayload(
        string text,
        string? detectedLanguage,
        double durationSeconds,
        string? modelId,
        string? profileName,
        DateTimeOffset timestamp
    ) =>
        JsonSerializer.Serialize(
            new
            {
                text,
                detectedLanguage,
                durationSeconds,
                modelId,
                profileName,
                timestamp,
            },
            s_jsonOptions
        );

    private static bool IsSameDestination(WebhookConfig current, WebhookConfig original) =>
        current.Url == original.Url
        && current.HttpMethod == original.HttpMethod
        && current.HeaderSecretReferences.Count == original.HeaderSecretReferences.Count
        && current.HeaderSecretReferences.All(pair =>
            original.HeaderSecretReferences.TryGetValue(pair.Key, out var reference)
            && reference == pair.Value
        );

    /// <summary>
    /// Sends <paramref name="json"/> to <paramref name="webhook"/> and logs the
    /// outcome. Returns null without logging when the delivery was abandoned:
    /// the caller cancelled, the service was disposed, or the webhook no
    /// longer passes <paramref name="stillWanted"/>.
    /// </summary>
    private async Task<DeliveryLogEntry?> DeliverAsync(
        WebhookConfig webhook,
        string json,
        Func<WebhookConfig, bool> stillWanted,
        int maxAttempts,
        bool isTest,
        CancellationToken deadline,
        CancellationToken callerToken
    )
    {
        var outcome = DeliveryOutcome.Failed;
        int? statusCode = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var current = SnapshotWebhooks().FirstOrDefault(candidate => candidate.Id == webhook.Id);
            if (current is null || !stillWanted(current))
                return null;

            bool retryable;
            try
            {
                (outcome, statusCode, retryable) = await SendOnceAsync(current, json, deadline);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                if (_lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
                    return null;

                (outcome, statusCode, retryable) = (DeliveryOutcome.TimedOut, null, false);
            }

            if (!retryable || attempt == maxAttempts)
                break;

            try
            {
                await Task.Delay(_retryDelay, deadline);
            }
            catch (OperationCanceledException)
            {
                if (_lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
                    return null;

                outcome = DeliveryOutcome.TimedOut;
                statusCode = null;
                break;
            }
        }

        var entry = new DeliveryLogEntry
        {
            WebhookName = webhook.Name,
            Outcome = outcome,
            StatusCode = statusCode,
            IsTest = isTest,
        };
        AddLogEntry(entry);
        if (!entry.Success && !isTest)
        {
            _host.Log(
                PluginLogLevel.Warning,
                $"Webhook delivery failed: {DescribeForLog(entry)}."
            );
        }

        return entry;
    }

    private async Task<(DeliveryOutcome Outcome, int? StatusCode, bool Retryable)> SendOnceAsync(
        WebhookConfig webhook,
        string json,
        CancellationToken ct
    )
    {
        var method = webhook.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Put
            : HttpMethod.Post;

        try
        {
            var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in webhook.HeaderSecretReferences)
            {
                var value = await _host.LoadSecretAsync(header.Value).WaitAsync(ct);
                if (value is null)
                    return (DeliveryOutcome.HeaderSecretUnavailable, null, false);

                resolvedHeaders[header.Key] = value;
            }

            using var request = new HttpRequestMessage(method, webhook.Url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            foreach (var header in resolvedHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return (DeliveryOutcome.Delivered, statusCode, false);

            // 429 and 503 say the request was refused rather than acted on.
            var refused = response.StatusCode
                is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable;
            return (DeliveryOutcome.HttpError, statusCode, refused);
        }
        catch (HttpRequestException ex)
            when (ex.HttpRequestError
                    is HttpRequestError.NameResolutionError
                        or HttpRequestError.ConnectionError
            )
        {
            // The connection was never established, so nothing was sent.
            return (DeliveryOutcome.ConnectionFailed, null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            // E.g. the client was disposed by deactivation mid-send: not a delivery failure.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (DeliveryOutcome.Failed, null, false);
        }
    }

    internal static string DescribeForLog(DeliveryLogEntry entry) =>
        entry.Outcome switch
        {
            DeliveryOutcome.Delivered or DeliveryOutcome.HttpError => $"HTTP {entry.StatusCode}",
            DeliveryOutcome.TimedOut => "timed out",
            DeliveryOutcome.ConnectionFailed => "connection failed",
            DeliveryOutcome.HeaderSecretUnavailable => "header secret unavailable",
            _ => "request failed",
        };

    private void AddLogEntry(DeliveryLogEntry entry)
    {
        lock (_logLock)
        {
            _deliveryLog.Insert(0, entry);
            if (_deliveryLog.Count > MaxLogEntries)
                _deliveryLog.RemoveRange(MaxLogEntries, _deliveryLog.Count - MaxLogEntries);
        }
    }

    internal async Task MigrateLegacyHeadersAsync()
    {
        List<WebhookConfig> previous;
        lock (_webhooksLock)
            previous = Webhooks.ToList();

        if (previous.All(config => config.LegacyHeaders.Count == 0))
            return;

        try
        {
            var migrated = new List<WebhookConfig>(previous.Count);
            foreach (var config in previous)
            {
                var references = new Dictionary<string, string>(
                    config.HeaderSecretReferences,
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var header in config.LegacyHeaders)
                {
                    var reference = WebhookPlugin.GetHeaderSecretReference(
                        config.Id,
                        header.Key
                    );
                    await _host.StoreSecretAsync(reference, header.Value);
                    references[header.Key] = reference;
                }

                migrated.Add(
                    config with
                    {
                        HeaderSecretReferences = references,
                        LegacyHeaders = [],
                    }
                );
            }

            Save(migrated);
            lock (_webhooksLock)
            {
                Webhooks.Clear();
                foreach (var config in migrated)
                    Webhooks.Add(config);
            }

            var obsoleteReferences = previous
                .SelectMany(config => config.HeaderSecretReferences.Values)
                .Except(
                    migrated.SelectMany(config => config.HeaderSecretReferences.Values),
                    StringComparer.Ordinal
                )
                .ToList();
            foreach (var reference in obsoleteReferences)
            {
                try
                {
                    await _host.DeleteSecretAsync(reference);
                }
                catch (Exception ex)
                {
                    _host.Log(
                        PluginLogLevel.Warning,
                        $"Failed to delete obsolete webhook header secret: {ex.Message}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _host.Log(
                PluginLogLevel.Warning,
                $"Failed to migrate webhook header secrets: {ex.Message}"
            );
            throw;
        }
    }

    private void Load(bool protectExistingFile)
    {
        List<WebhookConfig> loaded;
        try
        {
            loaded = _store.Load(protectExistingFile);
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

    public void Dispose()
    {
        _lifetime.Cancel();
        _httpClient.Dispose();
    }
}

public sealed class WebhookPlugin
    : ITypeWhisperPlugin,
        IPluginCollectionSettingsProvider,
        IPluginSettingsActions,
        IPluginDataLocationAware,
        IPluginLocalizationAware
{
    internal const string StoredHeaderPlaceholder = "<stored securely>";
    internal const string AllProfilesMode = "all";
    internal const string ListedProfilesMode = "listed";
    private const string TestActionPrefix = "test:";
    private const string RecentDeliveriesActionId = "recent-deliveries";

    private IDisposable? _subscription;
    private string? _dataDirectory;
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private readonly Func<IPluginHostServices, string, WebhookService> _createService;

    public WebhookPlugin()
        : this((host, dataDirectory) => new WebhookService(host, dataDirectory)) { }

    // Test seam: lets tests deliver through a fake HTTP handler.
    internal WebhookPlugin(Func<IPluginHostServices, string, WebhookService> createService)
    {
        _createService = createService;
    }

    public string PluginId => "com.typewhisper.webhook";
    public string PluginName => "Webhook";
    public string PluginVersion => PluginBuildInfo.Version;

    public WebhookService? Service { get; private set; }

    public async Task ActivateAsync(IPluginHostServices host)
    {
        Host = host;
        // Single canonical data dir: prefer the one set via SetDataDirectory
        // (called by the loader before ActivateAsync); fall back to the host's
        // value for hosts that don't drive IPluginDataLocationAware. Threading
        // the same string through WebhookService and ResolveDataDir() keeps
        // the live service and any on-disk fallback path reading/writing the
        // same webhooks.json.
        _dataDirectory ??= host.PluginDataDirectory;
        var service = _createService(host, _dataDirectory);
        try
        {
            await service.MigrateLegacyHeadersAsync();
            Service = service;
            _subscription = host.EventBus.Subscribe<TranscriptionCompletedEvent>(
                OnTranscriptionCompleted
            );
        }
        catch
        {
            service.Dispose();
            Host = null;
            throw;
        }
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
                        IsSecret: true,
                        Description: Loc.L("Settings.HeadersDescription"),
                        Kind: PluginSettingKind.Multiline
                    ),
                    new PluginSettingDefinition(
                        "profileMode",
                        Loc.L("Settings.ProfileMode"),
                        Options:
                        [
                            new PluginSettingOption(AllProfilesMode, Loc.L("Settings.ProfileModeAll")),
                            new PluginSettingOption(ListedProfilesMode, Loc.L("Settings.ProfileModeListed")),
                        ],
                        Kind: PluginSettingKind.Dropdown
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
                source = new WebhookStore(ResolveDataDir()).Load(protectExistingFile: true);
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
                    ["headers"] = SerializeStoredHeaders(c),
                    ["profileMode"] = c.AllProfiles ? AllProfilesMode : ListedProfilesMode,
                    ["profiles"] = SerializeProfiles(c.ProfileFilter),
                    ["enabled"] = c.IsEnabled ? "true" : "false",
                    ["__id"] = c.Id.ToString("D"),
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
        if (collectionKey != "webhooks")
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.UnknownCollection")
            );

        var parsedItems = new List<ParsedWebhookItem>(items.Count);

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
            // HttpClient ignores URL credentials, and webhooks.json would hold them in plain text.
            if (parsedUrl.UserInfo.Length > 0)
                return Fail(label, Loc.L("Settings.UrlCredentials"));

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

            var profiles = ParseProfiles(Get(item, "profiles") ?? "");
            var rawProfileMode = (Get(item, "profileMode") ?? "").Trim();
            bool allProfiles;
            switch (rawProfileMode)
            {
                case "":
                    allProfiles = profiles.Count == 0;
                    break;
                case AllProfilesMode:
                    allProfiles = true;
                    break;
                case ListedProfilesMode:
                    allProfiles = false;
                    break;
                default:
                    return Fail(label, Loc.L("Settings.ProfileModeInvalid"));
            }

            // Each mode must agree with the list, so emptying the list of a
            // filtered webhook cannot quietly widen it to every profile.
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- two guard clauses; a switch on a tuple reads worse.
            if (allProfiles && profiles.Count > 0)
                return Fail(label, Loc.L("Settings.ProfilesWithAllMode"));
            if (!allProfiles && profiles.Count == 0)
                return Fail(label, Loc.L("Settings.ProfilesRequired"));

            parsedItems.Add(
                new ParsedWebhookItem(
                    new WebhookConfig
                    {
                        Id = id,
                        Name = name,
                        Url = url,
                        HttpMethod = method,
                        AllProfiles = allProfiles,
                        ProfileFilter = profiles,
                        IsEnabled = enabled,
                    },
                    headers
                )
            );
        }

        await _settingsSaveLock.WaitAsync(ct);
        try
        {
            var existing = Service is not null
                ? Service.SnapshotWebhooks()
                : new WebhookStore(ResolveDataDir()).Load(protectExistingFile: true);

            // Legacy plaintext headers only load into LegacyHeaders and are
            // never surfaced by GetItems, so an unrelated edit saved before
            // activation would rewrite the config without them and silently
            // destroy the headers. Fail closed until the plugin is activated
            // and MigrateLegacyHeadersAsync moves them into the secret store.
            if (existing.Any(config => config.LegacyHeaders.Count > 0))
            {
                throw new InvalidOperationException(
                    "Webhook header values require activated host secret services."
                );
            }

            var existingById = existing
                .GroupBy(config => config.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var unknownProfileFailure = FindUnknownProfile(parsedItems, existingById);
            if (unknownProfileFailure is not null)
                return unknownProfileFailure;

            var configs = new List<WebhookConfig>(parsedItems.Count);
            var pendingSecretWrites = new List<(string Reference, string Value)>();

            foreach (var parsedItem in parsedItems)
            {
                existingById.TryGetValue(parsedItem.Config.Id, out var existingConfig);
                var references = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );

                foreach (var header in parsedItem.HeaderValues)
                {
                    string? existingReference = null;
                    var hasExistingReference =
                        existingConfig is not null
                        && TryGetHeaderSecretReference(
                            existingConfig.HeaderSecretReferences,
                            header.Key,
                            out existingReference
                        );

                    string reference;
                    if (header.Value == StoredHeaderPlaceholder)
                    {
                        // GetItems renders stored values as this placeholder, so untouched
                        // headers return it unchanged. With nothing stored behind it — a
                        // duplicated row, a renamed header — it would be sent as the value.
                        if (!hasExistingReference)
                        {
                            return Fail(
                                parsedItem.Config.Name,
                                Loc.L("Settings.HeaderPlaceholderWithoutStoredValue", header.Key)
                            );
                        }

                        reference = existingReference!;
                    }
                    else
                    {
                        reference = GetHeaderSecretReference(
                            parsedItem.Config.Id,
                            header.Key
                        );
                        pendingSecretWrites.Add((reference, header.Value));
                    }

                    references[header.Key] = reference;
                }

                configs.Add(
                    parsedItem.Config with
                    {
                        HeaderSecretReferences = references,
                    }
                );
            }

            var newReferences = configs
                .SelectMany(config => config.HeaderSecretReferences.Values)
                .ToHashSet(StringComparer.Ordinal);
            var obsoleteReferences = existing
                .SelectMany(config => config.HeaderSecretReferences.Values)
                .Where(reference => !newReferences.Contains(reference))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (
                Host is null
                && (pendingSecretWrites.Count > 0 || obsoleteReferences.Count > 0)
            )
            {
                throw new InvalidOperationException(
                    "Webhook header values require activated host secret services."
                );
            }

            foreach (var (reference, value) in pendingSecretWrites)
                await Host!.StoreSecretAsync(reference, value);

            if (Service is not null)
                Service.ReplaceAll(configs);
            else
                new WebhookStore(ResolveDataDir()).Save(configs);

            foreach (var reference in obsoleteReferences)
            {
                try
                {
                    await Host!.DeleteSecretAsync(reference);
                }
                catch (Exception ex)
                {
                    Host!.Log(
                        PluginLogLevel.Warning,
                        $"Failed to delete obsolete webhook header secret: {ex.Message}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.FailedToSaveSettings", ex.Message)
            );
        }
        finally
        {
            _settingsSaveLock.Release();
        }

        return new PluginSettingsValidationResult(true, Loc.L("Settings.Saved"));

        PluginSettingsValidationResult Fail(string label, string reason) =>
            new(
                false,
                Loc.L("Settings.WebhookLabelReason", label, reason)
            );
    }

    /// <summary>
    /// Rejects a newly listed profile name that no profile has, which would
    /// otherwise never match. Names saved earlier are kept even after their
    /// profile is renamed or deleted, so unrelated edits still save.
    /// </summary>
    private PluginSettingsValidationResult? FindUnknownProfile(
        IEnumerable<ParsedWebhookItem> parsedItems,
        Dictionary<Guid, WebhookConfig> existingById
    )
    {
        if (Host is null)
            return null;

        HashSet<string>? available = null;
        foreach (var parsedItem in parsedItems.Where(item => item.Config.ProfileFilter.Count > 0))
        {
            available ??= Host.AvailableProfileNames.ToHashSet(StringComparer.Ordinal);
            existingById.TryGetValue(parsedItem.Config.Id, out var existingConfig);
            var unknown = parsedItem.Config.ProfileFilter.FirstOrDefault(name =>
                !available.Contains(name)
                && existingConfig?.ProfileFilter.Contains(name) != true
            );
            if (unknown is null)
                continue;

            var reason = available.Count == 0
                ? Loc.L("Settings.ProfileUnknownNoProfiles", unknown)
                : Loc.L(
                    "Settings.ProfileUnknown",
                    unknown,
                    string.Join(", ", Host.AvailableProfileNames)
                );
            return new PluginSettingsValidationResult(
                false,
                Loc.L("Settings.WebhookLabelReason", parsedItem.Config.Name, reason)
            );
        }

        return null;
    }

    public IReadOnlyList<PluginSettingsAction> GetSettingsActions()
    {
        if (Service is null)
            return [];

        var actions = Service
            .SnapshotWebhooks()
            .Select(webhook => new PluginSettingsAction(
                TestActionPrefix + webhook.Id.ToString("N"),
                Loc.L("Actions.Test", webhook.Name),
                Loc.L("Actions.TestDescription")
            ))
            .ToList();
        actions.Add(
            new PluginSettingsAction(RecentDeliveriesActionId, Loc.L("Actions.RecentDeliveries"))
        );
        return actions;
    }

    public async Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(
        string actionId,
        CancellationToken ct
    )
    {
        var service =
            Service ?? throw new InvalidOperationException("Webhook plugin is not active.");

        if (actionId == RecentDeliveriesActionId)
            return new PluginSettingsValidationResult(true, FormatDeliveryLog(service.SnapshotDeliveryLog()));

        if (
            !actionId.StartsWith(TestActionPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(actionId[TestActionPrefix.Length..], "N", out var webhookId)
        )
            throw new ArgumentException($"Unknown webhook action '{actionId}'.", nameof(actionId));

        var entry = await service.SendTestAsync(webhookId, ct);
        ct.ThrowIfCancellationRequested();
        return entry is null
            ? new PluginSettingsValidationResult(false, Loc.L("Actions.TestWebhookChanged"))
            : new PluginSettingsValidationResult(entry.Success, DescribeOutcome(entry));
    }

    private string FormatDeliveryLog(IReadOnlyList<DeliveryLogEntry> entries)
    {
        if (entries.Count == 0)
            return Loc.L("Actions.NoDeliveries");

        return string.Join(
            "\n",
            entries.Select(entry =>
                Loc.L(
                    entry.IsTest ? "Actions.DeliveryLogTestLine" : "Actions.DeliveryLogLine",
                    entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    entry.WebhookName,
                    DescribeOutcome(entry)
                )
            )
        );
    }

    private string DescribeOutcome(DeliveryLogEntry entry) =>
        entry.Outcome switch
        {
            DeliveryOutcome.Delivered => Loc.L("Outcome.Delivered", entry.StatusCode ?? 0),
            DeliveryOutcome.HttpError => Loc.L("Outcome.HttpError", entry.StatusCode ?? 0),
            DeliveryOutcome.TimedOut => Loc.L("Outcome.TimedOut"),
            DeliveryOutcome.ConnectionFailed => Loc.L("Outcome.ConnectionFailed"),
            DeliveryOutcome.HeaderSecretUnavailable => Loc.L("Outcome.HeaderSecretUnavailable"),
            _ => Loc.L("Outcome.Failed"),
        };

    private sealed record ParsedWebhookItem(
        WebhookConfig Config,
        Dictionary<string, string> HeaderValues
    );

    private static string? Get(PluginCollectionItem item, string key) =>
        item.Values.GetValueOrDefault(key);

    private static bool TryGetBool(PluginCollectionItem item, string key, out bool value)
    {
        var raw = Get(item, key);
        if (raw is not null && bool.TryParse(raw, out value))
            return true;
        value = false;
        return false;
    }

    internal static string GetHeaderSecretReference(Guid webhookId, string headerName) =>
        $"webhook-header:{webhookId:N}:{NormalizeHeaderName(headerName)}";

    private static string NormalizeHeaderName(string headerName) =>
        headerName.Trim().ToLowerInvariant();

    private static bool TryGetHeaderSecretReference(
        IReadOnlyDictionary<string, string> references,
        string headerName,
        out string? reference
    )
    {
        var normalizedName = NormalizeHeaderName(headerName);
        foreach (var candidate in references)
        {
            // ReSharper disable once InvertIf -- the positive form states the header match that ends the search.
            if (NormalizeHeaderName(candidate.Key) == normalizedName)
            {
                reference = candidate.Value;
                return true;
            }
        }

        reference = null;
        return false;
    }

    /// <summary>Serializes stored header names with a redacted value placeholder.</summary>
    internal static string SerializeStoredHeaders(WebhookConfig config)
    {
        return string.Join(
            "\n",
            config.HeaderSecretReferences.Keys.Select(
                name => $"{name}: {StoredHeaderPlaceholder}"
            )
        );
    }

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
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        // ReSharper disable once ConvertIfStatementToReturnStatement -- subjective style; kept as an explicit if.
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}
