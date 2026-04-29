using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Linear;

public sealed partial class LinearPlugin : IActionPlugin, IPluginSettingsProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _defaultTeamId;
    private string? _defaultProjectId;
    private List<LinearTeam> _cachedTeams = [];

    public string PluginId => "com.typewhisper.linear";
    public string PluginName => "Linear";
    public string PluginVersion => "1.0.0";

    public string ActionId => "create-linear-issue";
    public string ActionName => "Create Linear Issue";
    public string? ActionIcon => "\U0001F4CB";

    public IPluginHostServices? Host => _host;
    public string? ApiKey => _apiKey;
    public string? DefaultTeamId => _defaultTeamId;
    public string? DefaultProjectId => _defaultProjectId;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _defaultTeamId = host.GetSetting<string>("default-team-id");
        _defaultProjectId = host.GetSetting<string>("default-project-id");
        var cachedTeamsJson = host.GetSetting<string>("cached-teams");
        if (!string.IsNullOrWhiteSpace(cachedTeamsJson))
        {
            try { _cachedTeams = JsonSerializer.Deserialize<List<LinearTeam>>(cachedTeamsJson, s_jsonOptions) ?? []; }
            catch { _cachedTeams = []; }
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        host.Log(PluginLogLevel.Info, "Linear plugin activated");
    }

    public Task DeactivateAsync()
    {
        _host?.Log(PluginLogLevel.Info, "Linear plugin deactivated");
        return Task.CompletedTask;
    }

    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ActionResult(false, "Linear API key not configured. Please set it in plugin settings.");

        if (string.IsNullOrWhiteSpace(_defaultTeamId))
            return new ActionResult(false, "Default team ID not configured. Please set it in plugin settings.");

        var title = ExtractTitle(input);
        var description = input;

        try
        {
            var issueUrl = await CreateIssueAsync(title, description, ct);

            if (issueUrl is not null)
                return new ActionResult(true, $"Linear issue created: {title}", Url: issueUrl, DisplayDuration: 5.0);

            return new ActionResult(false, "Failed to create Linear issue. Check logs for details.");
        }
        catch (OperationCanceledException)
        {
            return new ActionResult(false, "Issue creation was cancelled.");
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Error, $"Failed to create Linear issue: {ex.Message}");
            return new ActionResult(false, $"Error creating issue: {ex.Message}");
        }
    }

    public async Task SaveApiKeyAsync(string apiKey)
    {
        if (_host is null)
            return;

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            await _host.DeleteSecretAsync("api-key");
        else
            await _host.StoreSecretAsync("api-key", apiKey.Trim());

        _host.NotifyCapabilitiesChanged();
        _host.Log(PluginLogLevel.Info, "Linear API key saved");
    }

    public void SaveDefaultTeamId(string teamId)
    {
        _defaultTeamId = string.IsNullOrWhiteSpace(teamId) ? null : teamId.Trim();
        _host?.SetSetting("default-team-id", _defaultTeamId ?? "");
    }

    public void SaveDefaultProjectId(string projectId)
    {
        _defaultProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        _host?.SetSetting("default-project-id", _defaultProjectId ?? "");
    }

    public async Task<List<LinearTeam>> FetchTeamsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return [];

        const string query = """
            query {
                teams {
                    nodes {
                        id
                        name
                        key
                    }
                }
            }
            """;

        var response = await SendGraphQlAsync(query, ct);
        if (response is null) return [];

        try
        {
            var data = response.Value.GetProperty("data").GetProperty("teams").GetProperty("nodes");
            var teams = new List<LinearTeam>();

            foreach (var node in data.EnumerateArray())
            {
                teams.Add(new LinearTeam
                {
                    Id = node.GetProperty("id").GetString() ?? "",
                    Name = node.GetProperty("name").GetString() ?? "",
                    Key = node.GetProperty("key").GetString() ?? ""
                });
            }

            _cachedTeams = teams;
            try
            {
                _host?.SetSetting("cached-teams", JsonSerializer.Serialize(teams, s_jsonOptions));
            }
            catch
            {
                // best effort cache
            }

            return teams;
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Failed to parse teams response: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> CreateIssueAsync(string title, string description, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["teamId"] = _defaultTeamId
        };

        if (!string.IsNullOrWhiteSpace(_defaultProjectId))
            variables["projectId"] = _defaultProjectId;

        const string mutation = """
            mutation IssueCreate($title: String!, $description: String, $teamId: String!, $projectId: String) {
                issueCreate(input: {
                    title: $title
                    description: $description
                    teamId: $teamId
                    projectId: $projectId
                }) {
                    success
                    issue {
                        id
                        identifier
                        url
                    }
                }
            }
            """;

        var response = await SendGraphQlAsync(mutation, ct, variables);
        if (response is null) return null;

        try
        {
            var issueCreate = response.Value.GetProperty("data").GetProperty("issueCreate");
            var success = issueCreate.GetProperty("success").GetBoolean();

            if (!success)
            {
                _host?.Log(PluginLogLevel.Warning, "Linear API returned success=false for issueCreate");
                return null;
            }

            var issue = issueCreate.GetProperty("issue");
            var url = issue.GetProperty("url").GetString();
            var identifier = issue.GetProperty("identifier").GetString();

            _host?.Log(PluginLogLevel.Info, $"Created Linear issue {identifier}");
            return url;
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Failed to parse issue creation response: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> SendGraphQlAsync(
        string query,
        CancellationToken ct,
        Dictionary<string, object?>? variables = null)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };

        if (variables is not null)
            payload["variables"] = variables;

        var json = JsonSerializer.Serialize(payload, s_jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linear.app/graphql");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _host?.Log(PluginLogLevel.Error, $"Linear API error {(int)response.StatusCode}: {errorBody}");
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);

        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            var errorMsg = errors.EnumerateArray().FirstOrDefault().GetProperty("message").GetString();
            _host?.Log(PluginLogLevel.Error, $"Linear GraphQL error: {errorMsg}");
            return null;
        }

        return doc.RootElement;
    }

    private static string ExtractTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Untitled Issue";

        // Use the first line as the title
        var firstLine = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
            return "Untitled Issue";

        // Truncate to 100 characters
        return firstLine.Length > 100 ? firstLine[..100] : firstLine;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new("api-key", "API key", true, null, "Generate a personal API key in Linear Settings > API > Personal API keys."),
        new(
            "default-team-id",
            "Default team ID",
            Description: _cachedTeams.Count > 0
                ? $"Showing {_cachedTeams.Count} cached Linear team(s). Click Validate to refresh."
                : "Required. Team UUID where issues will be created. Click Validate after saving the API key to fetch teams.",
            Options: _cachedTeams.Count > 0
                ? _cachedTeams.Select(t => new PluginSettingOption(t.Id, $"{t.Key} - {t.Name}")).ToList()
                : null),
        new("default-project-id", "Default project ID", false, null, "Optional. Issues will be added to this project when set.")
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            "api-key" => _apiKey,
            "default-team-id" => _defaultTeamId,
            "default-project-id" => _defaultProjectId,
            _ => null,
        });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case "api-key":
                await SaveApiKeyAsync(value ?? string.Empty);
                break;
            case "default-team-id":
                SaveDefaultTeamId(value ?? string.Empty);
                break;
            case "default-project-id":
                SaveDefaultProjectId(value ?? string.Empty);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new PluginSettingsValidationResult(false, "Enter an API key first.");

        var teams = await FetchTeamsAsync(ct);
        if (teams.Count == 0)
            return new PluginSettingsValidationResult(false, "No teams found. Check your API key.");

        return new PluginSettingsValidationResult(true, $"Found {teams.Count} team(s). Team options refreshed.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed class LinearTeam
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";

    public override string ToString() => $"{Key} - {Name}";
}
