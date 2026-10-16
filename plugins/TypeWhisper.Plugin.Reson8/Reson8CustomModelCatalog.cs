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
    internal IReadOnlyList<Reson8CustomModel> FetchedCustomModels =>
        _catalog is { } catalog && catalog.Owner == Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey)
            ? catalog.Models
            : [];

    // One consistent copy, so a concurrent settings save can't mix two connections.
    private Reson8Connection CaptureConnection()
    {
        lock (_catalogLock)
        {
            return new Reson8Connection(
                ApiKey ?? "",
                CustomBaseUrl,
                CustomAuthHeader,
                _selectedModelId,
                Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey),
                FetchedCustomModels.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
        }
    }

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

        // Never send another account's or server's custom model ID.
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
            return catalog is { Owner.Length: > 0, Models: not null } && IsValidCatalog(catalog.Models) ? catalog : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void SetFetchedCustomModels(IReadOnlyList<Reson8CustomModel> models) =>
        CommitCatalog(CaptureConnection().Owner, models);

    // False when the connection changed after the load started; the catalog is then discarded.
    private bool CommitCatalog(string owner, IReadOnlyList<Reson8CustomModel> models)
    {
        lock (_catalogLock)
        {
            if (owner != Fingerprint(CustomBaseUrl, CustomAuthHeader, ApiKey))
                return false;

            var catalog = new Reson8CustomModelCatalog { Owner = owner, Models = models.ToList() };

            // Saved first and restored if the catalog write fails, so the two never disagree.
            var dropSelection = _selectedModelId != DefaultModelId && catalog.Models.All(m => m.Id != _selectedModelId);
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
            var before = FetchedCustomModels.Count > 0;
            change();
            return before != FetchedCustomModels.Count > 0;
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
        if (fetch.Models is null)
            return new PluginSettingsValidationResult(true, Loc.L("Settings.CustomModelsRefreshFailed", fetch.FailureReason!));

        if (!CommitCatalog(connection.Owner, fetch.Models))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.SettingsChangedDuringValidation"));

        return new PluginSettingsValidationResult(
            true,
            fetch.Models.Count > 0
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

    internal Task<Reson8CatalogFetch> FetchCustomModelsAsync(CancellationToken ct = default) =>
        FetchCustomModelsAsync(CaptureConnection(), ct);

    // Read-only. Models is null on any failure, so a failed load never replaces the previous list.
    private async Task<Reson8CatalogFetch> FetchCustomModelsAsync(Reson8Connection connection, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl}/v1/custom-model");
        AddAuthHeader(request, connection.ApiKey, connection.AuthHeader);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_validationRequestTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return Reson8CatalogFetch.Failed(Loc.L("Settings.RefreshReasonStatus", (int)response.StatusCode));

            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            var models = body is null ? null : ParseCatalogResponse(body);
            return models is null
                ? Reson8CatalogFetch.Failed(Loc.L("Settings.RefreshReasonInvalid"))
                : new Reson8CatalogFetch(models, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Reson8CatalogFetch.Failed(Loc.L("Settings.RefreshReasonUnreachable"));
        }
        // A body cut off mid-read surfaces as an IOException rather than HttpRequestException.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Reson8CatalogFetch.Failed(Loc.L("Settings.RefreshReasonUnreachable"));
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

internal sealed record Reson8Connection(
    string ApiKey,
    string BaseUrl,
    string AuthHeader,
    string ModelId,
    string Owner,
    IReadOnlySet<string> CatalogModelIds);

internal sealed record Reson8CatalogFetch(IReadOnlyList<Reson8CustomModel>? Models, string? FailureReason)
{
    public static Reson8CatalogFetch Failed(string reason) => new(null, reason);
}

public sealed class Reson8CustomModelCatalog
{
    public string Owner { get; init; } = "";
    public List<Reson8CustomModel> Models { get; init; } = [];
}
