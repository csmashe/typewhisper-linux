using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Linear;

public sealed partial class LinearPlugin
{
    private const string CatalogSettingKey = "selection-catalog";
    private const string LegacyCachedTeamsSettingKey = "cached-teams";
    private const int PageSize = 100;
    private const int MaxPages = 100;
    private const int MaxNameLength = 512;

    private LinearSelectionCatalog? _catalog;

    // Teams and projects are account data, so a catalog loaded with another key is never shown.
    private LinearSelectionCatalog? CurrentCatalog =>
        _catalog is not null && ApiKey is { Length: > 0 } key && _catalog.KeyFingerprint == KeyFingerprint(key)
            ? _catalog
            : null;

    private void RestoreCatalog(IPluginHostServices host)
    {
        var json = host.GetSetting<string>(CatalogSettingKey);
        if (string.IsNullOrWhiteSpace(json))
            return;

        LinearSelectionCatalog? catalog = null;
        try
        {
            catalog = JsonSerializer.Deserialize<LinearSelectionCatalog>(json, s_jsonOptions);
        }
        catch (JsonException)
        {
            // Reported below with the other unusable shapes.
        }

        // JSON nulls override the initializers, and the settings dropdowns dereference every entry.
        // ReSharper disable RedundantAlwaysMatchSubpattern -- non-nullable only by annotation; deserialization can still produce null here.
        if (catalog is { KeyFingerprint: not null, Teams: not null, Projects: not null }
            && catalog.Teams.All(t => t is { Id: not null, Name: not null, Key: not null })
            && catalog.Projects.All(p => p is { Id: not null, Name: not null }))
        // ReSharper restore RedundantAlwaysMatchSubpattern
        {
            _catalog = catalog;
            return;
        }

        host.Log(PluginLogLevel.Warning, "Ignoring unreadable cached Linear teams and projects.");
        _catalog = null;
    }

    // Read-only. Null when any page fails, so a partial list never replaces the previous one.
    private async Task<LinearSelectionCatalog?> RefreshCatalogAsync(CancellationToken ct)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var teams = await LoadAllAsync(apiKey, "teams", "id name key", ReadTeam, ct);
        if (teams is null)
            return null;

        var projects = await LoadAllAsync(apiKey, "projects", "id name", ReadProject, ct);
        if (projects is null)
            return null;

        if (ApiKey != apiKey)
        {
            Host?.Log(PluginLogLevel.Warning, "Linear API key changed while loading teams and projects; discarded.");
            return null;
        }

        var catalog = new LinearSelectionCatalog
        {
            KeyFingerprint = KeyFingerprint(apiKey),
            Teams = teams.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id, StringComparer.Ordinal).ToList(),
            Projects = projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList(),
        };
        _catalog = catalog;
        Host?.SetSetting(CatalogSettingKey, JsonSerializer.Serialize(catalog, s_jsonOptions));
        // The unscoped cache from earlier versions could belong to any key.
        Host?.SetSetting(LegacyCachedTeamsSettingKey, "");
        return catalog;
    }

    private async Task<List<T>?> LoadAllAsync<T>(
        string apiKey,
        string collection,
        string fields,
        Func<JsonElement, T?> readNode,
        CancellationToken ct
    )
        where T : class, ILinearChoice
    {
        var query = $$"""
            query Choices($after: String) {
                {{collection}}(first: {{PageSize}}, after: $after) {
                    nodes { {{fields}} }
                    pageInfo { hasNextPage endCursor }
                }
            }
            """;
        var items = new Dictionary<string, T>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var response = await SendGraphQlAsync(apiKey, query, ct, new Dictionary<string, object?> { ["after"] = cursor });
            if (response.Outcome != GraphQlOutcome.Succeeded)
                return null;

            if (!TryReadPage(response.Root, collection, readNode, items, out var hasNextPage, out var endCursor))
            {
                Host?.Log(PluginLogLevel.Warning, $"Linear returned an unexpected {collection} page; kept the previous list.");
                return null;
            }

            if (!hasNextPage)
                return items.Values.ToList();

            if (endCursor is null || !cursors.Add(endCursor))
            {
                Host?.Log(PluginLogLevel.Warning, $"Linear {collection} pagination did not advance; kept the previous list.");
                return null;
            }

            cursor = endCursor;
        }

        Host?.Log(PluginLogLevel.Warning, $"Linear {collection} exceed {MaxPages * PageSize}; kept the previous list.");
        return null;
    }

    private static bool TryReadPage<T>(
        JsonElement root,
        string collection,
        Func<JsonElement, T?> readNode,
        Dictionary<string, T> items,
        out bool hasNextPage,
        out string? endCursor
    )
        where T : class, ILinearChoice
    {
        hasNextPage = false;
        endCursor = null;
        if (
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(collection, out var connection) || connection.ValueKind != JsonValueKind.Object
            || !connection.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array
            || !connection.TryGetProperty("pageInfo", out var pageInfo) || pageInfo.ValueKind != JsonValueKind.Object
            || !pageInfo.TryGetProperty("hasNextPage", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            return false;
        }

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- stops at the first malformed node; LINQ would switch enumerators.
        foreach (var node in nodes.EnumerateArray())
        {
            var item = readNode(node);
            if (item is null)
                return false;
            items[item.Id] = item;
        }

        hasNextPage = more.GetBoolean();
        if (pageInfo.TryGetProperty("endCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
            endCursor = cursor.GetString();
        return true;
    }

    private static LinearTeam? ReadTeam(JsonElement node) =>
        ReadChoice(node, "id") is { } id && ReadChoice(node, "name") is { } name && ReadChoice(node, "key") is { } key
            ? new LinearTeam { Id = id, Name = name, Key = key }
            : null;

    private static LinearProject? ReadProject(JsonElement node) =>
        ReadChoice(node, "id") is { } id && ReadChoice(node, "name") is { } name
            ? new LinearProject { Id = id, Name = name }
            : null;

    private static string? ReadChoice(JsonElement node, string property)
    {
        if (
            node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxNameLength || text.Any(char.IsControl))
            return null;
        return property == "id" && !Guid.TryParse(text, out _) ? null : text;
    }

    private static string KeyFingerprint(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("TypeWhisper.Linear.catalog/1\n" + apiKey)));
}

internal interface ILinearChoice
{
    string Id { get; }
}

public sealed class LinearSelectionCatalog
{
    public string KeyFingerprint { get; init; } = "";
    public List<LinearTeam> Teams { get; init; } = [];
    public List<LinearProject> Projects { get; init; } = [];
}

public sealed class LinearTeam : ILinearChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";

    public override string ToString() => $"{Key} - {Name}";
}

public sealed class LinearProject : ILinearChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    public override string ToString() => Name;
}
