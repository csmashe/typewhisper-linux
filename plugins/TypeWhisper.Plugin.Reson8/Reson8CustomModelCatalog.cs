using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public sealed partial class Reson8Plugin
{
    private const string CatalogSettingName = "customModelCatalog";
    private const string LegacyCatalogSettingName = "fetchedCustomModels";
    private const int MaxCatalogBytes = 1024 * 1024;
    private const int MaxCatalogModels = 1000;
    private const int MaxModelIdLength = 256;
    private const int MaxModelTextLength = 512;
    private static readonly TimeSpan s_validationRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _catalogLock = new();
    private Reson8CustomModelCatalog? _catalog;

    // Custom models belong to the account and server they were loaded from, so a catalog
    // loaded with another key, base URL or auth header is never shown or used.
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels => VisibleCatalog?.Models ?? [];

    // Reson8's own models (GET /v1/model), kept with the same owner as the custom models.
    internal IReadOnlyList<Reson8Model> FetchedListedModels => VisibleCatalog?.ListedModels ?? [];

    private Reson8CustomModelCatalog? VisibleCatalog =>
        _catalog is { } catalog && catalog.Owner == Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey) ? catalog : null;

    private static Reson8Model? DefaultListedModel(IReadOnlyList<Reson8Model> listed) =>
        listed.FirstOrDefault(m => m.IsDefault == true);

    private static IEnumerable<Reson8Model> NonDefaultListedModels(IReadOnlyList<Reson8Model> listed) =>
        listed.Where(m => m.IsDefault != true);

    // One consistent copy, so a concurrent settings save can't mix two connections.
    private Reson8Connection CaptureConnection()
    {
        lock (_catalogLock)
        {
            var listed = FetchedListedModels;
            var listedModel = NonDefaultListedModels(listed).FirstOrDefault(m => m.Id == _selectedModelId);
            return new Reson8Connection(
                ApiKey ?? "",
                CustomBaseUrl,
                CustomAuthHeader,
                _selectedModelId,
                Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey),
                FetchedCustomModels.Select(m => m.Id)
                    .Concat(NonDefaultListedModels(listed).Select(m => m.Id))
                    .ToHashSet(StringComparer.Ordinal),
                listedModel,
                listedModel ?? DefaultListedModel(listed),
                FillerMode,
                BiasStrength);
        }
    }

    private static Reson8QueryOptions QueryOptionsFor(Reson8Connection connection) =>
        new(
            connection.ListedModel?.Id,
            Reson8Models.TakesParameter(connection.Model, "filler_mode") ? connection.FillerMode : Reson8FillerModes.Natural,
            connection.BiasStrength,
            Reson8Models.TakesParameter(connection.Model, "include_timestamps")
                && Reson8Models.TakesParameter(connection.Model, "include_words"));

    private string? ConnectionProblem(Reson8Connection connection)
    {
        if (connection.ApiKey.Any(char.IsControl))
            return Loc.L("Settings.InvalidApiKeyCharacters");
        if (!IsValidBaseUrl(connection.BaseUrl))
            return Loc.L("Settings.InvalidBaseUrl");
        return IsValidHeaderName(connection.AuthHeader) ? null : Loc.L("Settings.InvalidAuthHeader");
    }

    private Reson8Connection EnsureReadyForRequest()
    {
        var connection = CaptureConnection();
        if (connection.ApiKey.Length == 0)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);

        if (ConnectionProblem(connection) is { } problem)
            throw new PluginRequestException(problem, PluginRequestFailureKind.Configuration);

        // Never send another account's or server's model ID.
        if (connection.ModelId != DefaultModelId && !connection.CatalogModelIds.Contains(connection.ModelId))
            throw new PluginRequestException(Loc.L("Settings.SelectedModelUnavailable"), PluginRequestFailureKind.Configuration);

        return connection;
    }

    private void RestoreCatalog(IPluginHostServices host)
    {
        var json = host.GetSetting<string>(CatalogSettingName);
        if (json is not null)
        {
            _catalog = ParseStoredCatalog(json);
            if (_catalog is null)
                host.Log(PluginLogLevel.Warning, "Ignoring unreadable cached Reson8 custom models.");
            return;
        }

        // Earlier versions stored an unscoped list; it was loaded with the connection saved beside it.
        var legacy = host.GetSetting<List<Reson8CustomModel>>(LegacyCatalogSettingName);
        if (legacy is not { Count: > 0 } || !IsValidCatalog(legacy))
            return;

        _catalog = new Reson8CustomModelCatalog { Owner = Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey), Models = legacy };
        try
        {
            host.SetSetting(CatalogSettingName, JsonSerializer.Serialize(_catalog, s_jsonOptions));
        }
        catch (Exception ex)
        {
            host.Log(PluginLogLevel.Warning, $"Could not migrate cached Reson8 custom models ({ex.GetType().Name}).");
        }
    }

    private static Reson8CustomModelCatalog? ParseStoredCatalog(string json)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<Reson8CustomModelCatalog>(json, s_jsonOptions);
            // ReSharper disable once RedundantAlwaysMatchSubpattern, ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- non-nullable only by annotation; JSON null still deserializes to null.
            if (catalog is not { Owner.Length: > 0, Models: not null } || !IsValidCatalog(catalog.Models))
                return null;

            // Catalogs saved before the model list have none.
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- JSON null still deserializes to null.
            return catalog with { ListedModels = Reson8Models.Sanitize(catalog.ListedModels ?? []) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models) =>
        CommitCatalog(CaptureConnection().Owner, models, listedModels: null);

    internal void SetFetchedListedModels(IReadOnlyList<Reson8Model> models) =>
        CommitCatalog(CaptureConnection().Owner, customModels: null, models);

    // A null list keeps the current one. False when the connection changed after the load started;
    // the catalog is then discarded.
    private bool CommitCatalog(string owner, IReadOnlyList<Reson8CustomModel>? customModels, IReadOnlyList<Reson8Model>? listedModels)
    {
        lock (_catalogLock)
        {
            if (owner != Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey))
                return false;

            var catalog = new Reson8CustomModelCatalog
            {
                Owner = owner,
                Models = (customModels ?? FetchedCustomModels).ToList(),
                ListedModels = Reson8Models.Sanitize(listedModels ?? FetchedListedModels),
            };

            // Saved first and restored if the catalog write fails, so the two never disagree.
            var dropSelection = _selectedModelId != DefaultModelId
                && catalog.Models.All(m => m.Id != _selectedModelId)
                && NonDefaultListedModels(catalog.ListedModels).All(m => m.Id != _selectedModelId);
            if (dropSelection)
                _host?.SetSetting(SelectedModelSettingName, DefaultModelId);
            try
            {
                _host?.SetSetting(CatalogSettingName, JsonSerializer.Serialize(catalog, s_jsonOptions));
            }
            catch when (dropSelection)
            {
                _host?.SetSetting(SelectedModelSettingName, _selectedModelId);
                throw;
            }

            _catalog = catalog;
            if (dropSelection)
                _selectedModelId = DefaultModelId;
        }

        _host?.NotifyCapabilitiesChanged();
        return true;
    }

    // The visible catalog follows the connection; true when the change hid or revealed models.
    private bool ChangeConnection(Action change)
    {
        lock (_catalogLock)
        {
            var before = VisibleCatalog is { } shown && (shown.Models.Count > 0 || shown.ListedModels.Count > 0);
            change();
            var after = VisibleCatalog is { } visible && (visible.Models.Count > 0 || visible.ListedModels.Count > 0);
            return before != after;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var connection = CaptureConnection();
        if (connection.ApiKey.Length == 0)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        if (ConnectionProblem(connection) is { } problem)
            return new PluginSettingsValidationResult(false, problem);

        var keyProblem = await ProbeApiKeyAsync(connection, ct) switch
        {
            Reson8KeyCheck.Invalid => "Settings.InvalidApiKey",
            Reson8KeyCheck.Unreachable => "Settings.ServerUnreachable",
            Reson8KeyCheck.Redirected => "Settings.ServerRedirected",
            _ => null,
        };
        if (keyProblem is not null)
            return new PluginSettingsValidationResult(false, Loc.L(keyProblem));

        var fetch = await FetchCustomModelsAsync(connection, ct);
        var listedFetch = await FetchListedModelsAsync(connection, ct);

        // A list that couldn't be loaded keeps its previous entries.
        if ((fetch.Models is not null || listedFetch.Models is not null)
            && !CommitCatalog(connection.Owner, fetch.Models, listedFetch.Models))
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.SettingsChangedDuringValidation"));
        }

        if ((fetch.FailureReason ?? listedFetch.FailureReason) is { } failure)
            return new PluginSettingsValidationResult(true, Loc.L("Settings.CustomModelsRefreshFailed", failure));

        return new PluginSettingsValidationResult(
            true,
            fetch.Models!.Count > 0
                ? Loc.L("Settings.ApiKeyValidWithModels", fetch.Models.Count)
                : Loc.L("Settings.ApiKeyValidShort"));
    }

    internal Task<Reson8KeyCheck> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        ProbeApiKeyAsync(CaptureConnection() with { ApiKey = apiKey.Trim() }, ct);

    // Mirrors the macOS check: an empty upload proves the key unless the server answers 401.
    private async Task<Reson8KeyCheck> ProbeApiKeyAsync(Reson8Connection connection, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPrerecordedUri(connection.BaseUrl, DefaultModelId, language: null));
        AddAuthHeader(request, connection.ApiKey, connection.AuthHeader);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_validationRequestTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Reson8KeyCheck.Invalid,
                >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => Reson8KeyCheck.Redirected,
                _ => Reson8KeyCheck.Valid,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Reson8KeyCheck.Unreachable;
        }
        catch (HttpRequestException)
        {
            return Reson8KeyCheck.Unreachable;
        }
    }

    internal Task<Reson8CatalogFetch<Reson8CustomModel>> FetchCustomModelsAsync(CancellationToken ct = default) =>
        FetchCustomModelsAsync(CaptureConnection(), ct);

    private Task<Reson8CatalogFetch<Reson8CustomModel>> FetchCustomModelsAsync(Reson8Connection connection, CancellationToken ct) =>
        FetchListAsync(connection, "/v1/custom-model", ParseCatalogResponse, missingListIsUnchanged: false, ct);

    // A server without the model list (a proxy, an older deployment) answers 404; whatever list is
    // cached stays, and with none the default model runs.
    private Task<Reson8CatalogFetch<Reson8Model>> FetchListedModelsAsync(Reson8Connection connection, CancellationToken ct) =>
        FetchListAsync(connection, "/v1/model", Reson8Models.ParseModelListResponse, missingListIsUnchanged: true, ct);

    // Read-only. Models is null on any failure, so a failed load never replaces the previous list.
    private async Task<Reson8CatalogFetch<T>> FetchListAsync<T>(
        Reson8Connection connection,
        string path,
        Func<byte[], List<T>?> parse,
        bool missingListIsUnchanged,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl}{path}");
        AddAuthHeader(request, connection.ApiKey, connection.AuthHeader);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_validationRequestTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (missingListIsUnchanged && response.StatusCode == HttpStatusCode.NotFound)
                return new Reson8CatalogFetch<T>(null, null);
            if (!response.IsSuccessStatusCode)
                return Reson8CatalogFetch<T>.Failed(Loc.L("Settings.RefreshReasonStatus", (int)response.StatusCode));

            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            var models = body is null ? null : parse(body);
            return models is null
                ? Reson8CatalogFetch<T>.Failed(Loc.L("Settings.RefreshReasonInvalid"))
                : new Reson8CatalogFetch<T>(models, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Reson8CatalogFetch<T>.Failed(Loc.L("Settings.RefreshReasonUnreachable"));
        }
        // A body cut off mid-read surfaces as an IOException rather than HttpRequestException.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Reson8CatalogFetch<T>.Failed(Loc.L("Settings.RefreshReasonUnreachable"));
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxCatalogBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxCatalogBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    internal static List<Reson8CustomModel>? ParseCatalogResponse(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var models = new List<Reson8CustomModel>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGetOptional(item, "description", JsonValueKind.String, out var description)
                    || !TryGetOptional(item, "phraseCount", JsonValueKind.Number, out var phraseCount))
                {
                    return null;
                }

                int? phrases = null;
                if (phraseCount is { } count)
                {
                    if (!count.TryGetInt32(out var value))
                        return null;
                    phrases = value;
                }

                models.Add(new Reson8CustomModel(
                    GetString(item, "id")!,
                    GetString(item, "name")!,
                    description?.GetString(),
                    phrases));
            }

            return IsValidCatalog(models) ? models : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A present property must be null or of the expected kind.
    private static bool TryGetOptional(JsonElement item, string name, JsonValueKind kind, out JsonElement? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != kind)
            return false;

        value = property;
        return true;
    }

    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- non-nullable only by annotation; deserialization can still produce nulls here.
    private static bool IsValidCatalog(List<Reson8CustomModel> models) =>
        models.Count <= MaxCatalogModels
        && models.All(m => m is not null
            && IsValidModelText(m.Id, MaxModelIdLength)
            && m.Id != DefaultModelId
            && IsValidModelText(m.Name, MaxModelTextLength)
            && m.PhraseCount is null or >= 0)
        && models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == models.Count;
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

    private static bool IsValidModelText(string? text, int maxLength) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maxLength && !text.Any(char.IsControl);

    private static bool IsValidBaseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.Host.Length > 0
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;

    // RFC 9110 token characters.
    private static bool IsValidHeaderName(string header) =>
        header.Length > 0 && header.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    private static string Fingerprint(string baseUrl, string authHeader, string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"TypeWhisper.Reson8.catalog/1\n{baseUrl}\n{authHeader.ToLowerInvariant()}\n{apiKey}")));
}

internal enum Reson8KeyCheck
{
    Valid,
    Invalid,
    Unreachable,
    Redirected,
}

/// <param name="ListedModel">The selected model from Reson8's list; null for the default or a custom model.</param>
/// <param name="Model">The listed model the request runs on, or null while the list isn't loaded.</param>
internal sealed record Reson8Connection(
    string ApiKey,
    string BaseUrl,
    string AuthHeader,
    string ModelId,
    string Owner,
    IReadOnlySet<string> CatalogModelIds,
    Reson8Model? ListedModel,
    Reson8Model? Model,
    string FillerMode,
    double? BiasStrength)
{
    public bool IsCustomModel => ListedModel is null && ModelId != Reson8Plugin.DefaultModelId;
}

/// <summary>What a request asks for beyond its language, custom model and phrases.</summary>
/// <param name="Model">A model from Reson8's list, sent as <c>model</c>.</param>
internal sealed record Reson8QueryOptions(
    string? Model = null,
    string FillerMode = Reson8FillerModes.Natural,
    double? BiasStrength = null,
    bool IncludeWords = false);

internal sealed record Reson8CatalogFetch<T>(IReadOnlyList<T>? Models, string? FailureReason)
{
    public static Reson8CatalogFetch<T> Failed(string reason) => new(null, reason);
}

public sealed record Reson8CustomModelCatalog
{
    public string Owner { get; init; } = "";
    public List<Reson8CustomModel> Models { get; init; } = [];
    public List<Reson8Model> ListedModels { get; init; } = [];
}
