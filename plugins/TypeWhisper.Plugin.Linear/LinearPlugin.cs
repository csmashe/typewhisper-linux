// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Linear;

public sealed class LinearPlugin : IActionPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private List<LinearTeam> _cachedTeams = [];

    public LinearPlugin()
        : this(new HttpClient())
    {
    }

    internal LinearPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.linear";
    public string PluginName => "Linear";
    public string PluginVersion => PluginBuildInfo.Version;

    public string ActionId => "create-linear-issue";
    public string ActionName => "Create Linear Issue";
    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? ActionIcon => "\U0001F4CB";

    public IPluginHostServices? Host { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => Host?.Localization ?? _injectedLocalization;
    public string? ApiKey { get; private set; }

    public string? DefaultTeamId { get; private set; }

    public string? DefaultProjectId { get; private set; }

    public async Task ActivateAsync(IPluginHostServices host)
    {
        Host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        DefaultTeamId = host.GetSetting<string>("default-team-id");
        DefaultProjectId = host.GetSetting<string>("default-project-id");
        var cachedTeamsJson = host.GetSetting<string>("cached-teams");
        if (!string.IsNullOrWhiteSpace(cachedTeamsJson))
        {
            try
            {
                _cachedTeams =
                    JsonSerializer.Deserialize<List<LinearTeam>>(cachedTeamsJson, s_jsonOptions)
                    ?? [];
            }
            catch (JsonException ex)
            {
                host.Log(
                    PluginLogLevel.Warning,
                    $"Failed to deserialize cached teams; resetting cache: {ex.Message}"
                );
                _cachedTeams = [];
            }
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );

        host.Log(PluginLogLevel.Info, "Linear plugin activated");
    }

    public Task DeactivateAsync()
    {
        Host?.Log(PluginLogLevel.Info, "Linear plugin deactivated");
        return Task.CompletedTask;
    }

    public async Task<ActionResult> ExecuteAsync(
        string input,
        ActionContext context,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new ActionResult(
                false,
                Loc.L("Settings.ApiKeyNotConfigured")
            );

        if (string.IsNullOrWhiteSpace(DefaultTeamId))
            return new ActionResult(
                false,
                Loc.L("Settings.DefaultTeamNotConfigured")
            );

        var title = ExtractTitle(input);

        try
        {
            var issueUrl = await CreateIssueAsync(title, input, ct);

            if (issueUrl is not null)
                return new ActionResult(
                    true,
                    Loc.L("Settings.IssueCreated", title),
                    Url: issueUrl,
                    DisplayDuration: 5.0
                );

            return new ActionResult(
                false,
                Loc.L("Settings.IssueCreateFailed")
            );
        }
        catch (OperationCanceledException)
        {
            return new ActionResult(false, Loc.L("Settings.IssueCreateCancelled"));
        }
        catch (Exception ex)
        {
            Host?.Log(PluginLogLevel.Error, $"Failed to create Linear issue: {ex.Message}");
            return new ActionResult(false, Loc.L("Settings.IssueCreateError", ex.Message));
        }
    }

    public async Task SaveApiKeyAsync(string apiKey)
    {
        if (Host is null)
            return;

        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            await Host.DeleteSecretAsync("api-key");
        else
            await Host.StoreSecretAsync("api-key", apiKey.Trim());

        Host.NotifyCapabilitiesChanged();
        Host.Log(PluginLogLevel.Info, "Linear API key saved");
    }

    public void SaveDefaultTeamId(string teamId)
    {
        DefaultTeamId = string.IsNullOrWhiteSpace(teamId) ? null : teamId.Trim();
        Host?.SetSetting("default-team-id", DefaultTeamId ?? "");
    }

    public void SaveDefaultProjectId(string projectId)
    {
        DefaultProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        Host?.SetSetting("default-project-id", DefaultProjectId ?? "");
    }

    public async Task<List<LinearTeam>> FetchTeamsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
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
        if (response is null)
            return [];

        try
        {
            var data = response.Value.GetProperty("data").GetProperty("teams").GetProperty("nodes");
            var teams = new List<LinearTeam>();

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- explicit loop kept; the LINQ form switches enumerators.
            foreach (var node in data.EnumerateArray())
            {
                teams.Add(
                    new LinearTeam
                    {
                        Id = node.GetProperty("id").GetString() ?? "",
                        Name = node.GetProperty("name").GetString() ?? "",
                        Key = node.GetProperty("key").GetString() ?? "",
                    }
                );
            }

            _cachedTeams = teams;
            try
            {
                Host?.SetSetting("cached-teams", JsonSerializer.Serialize(teams, s_jsonOptions));
            }
            catch
            {
                // best effort cache
            }

            return teams;
        }
        catch (Exception ex)
        {
            Host?.Log(PluginLogLevel.Warning, $"Failed to parse teams response: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> CreateIssueAsync(
        string title,
        string description,
        CancellationToken ct
    )
    {
        var variables = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["teamId"] = DefaultTeamId,
        };

        if (!string.IsNullOrWhiteSpace(DefaultProjectId))
            variables["projectId"] = DefaultProjectId;

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
        if (response is null)
            return null;

        try
        {
            var issueCreate = response.Value.GetProperty("data").GetProperty("issueCreate");
            var success = issueCreate.GetProperty("success").GetBoolean();

            if (!success)
            {
                Host?.Log(
                    PluginLogLevel.Warning,
                    "Linear API returned success=false for issueCreate"
                );
                return null;
            }

            var issue = issueCreate.GetProperty("issue");
            var url = issue.GetProperty("url").GetString();
            var identifier = issue.GetProperty("identifier").GetString();

            Host?.Log(PluginLogLevel.Info, $"Created Linear issue {identifier}");
            return url;
        }
        catch (Exception ex)
        {
            Host?.Log(
                PluginLogLevel.Warning,
                $"Failed to parse issue creation response: {ex.Message}"
            );
            return null;
        }
    }

    private async Task<JsonElement?> SendGraphQlAsync(
        string query,
        CancellationToken ct,
        Dictionary<string, object?>? variables = null
    )
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };

        if (variables is not null)
            payload["variables"] = variables;

        var json = JsonSerializer.Serialize(payload, s_jsonOptions);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.linear.app/graphql"
        );
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation: propagate so ExecuteAsync's outer handler
            // can surface "cancelled" instead of swallowing it as a transport
            // failure.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout (not caller cancellation) surfaces as
            // TaskCanceledException — treat as transport failure.
            var fingerprint = ShortFingerprint(ex.ToString());
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API request timed out (sha256:{fingerprint})"
            );
            return null;
        }
        catch (HttpRequestException ex)
        {
            var fingerprint = ShortFingerprint(ex.ToString());
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API transport error: {ex.Message} (sha256:{fingerprint})"
            );
            return null;
        }

        using var responseScope = response;

        if (!response.IsSuccessStatusCode)
        {
            // Linear error bodies can echo back the failed mutation, including
            // issue titles/descriptions — log only status + a short fingerprint
            // of the response so the body is correlatable across reports
            // without leaking user content to the trace.
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // ReSharper disable once MergeIntoLogicalPattern -- subjective style; kept as-is.
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
            {
                var fp = ShortFingerprint(ex.ToString());
                Host?.Log(
                    PluginLogLevel.Error,
                    $"Linear API error {(int)response.StatusCode}; could not read body: {ex.Message} (sha256:{fp})"
                );
                return null;
            }
            var fingerprint = ShortFingerprint(errorBody);
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API error {(int)response.StatusCode} (body length={errorBody.Length}, sha256:{fingerprint})"
            );
            return null;
        }

        string responseJson;
        try
        {
            responseJson = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // ReSharper disable once MergeIntoLogicalPattern -- subjective style; kept as-is.
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
        {
            var fingerprint = ShortFingerprint(ex.ToString());
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API response read failed: {ex.Message} (sha256:{fingerprint})"
            );
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);

            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                // GraphQL error arrays should contain { "message": "..." } objects, but
                // be defensive: a missing/empty array or unexpected shape must not throw
                // and hide the original failure.
                string? errorMsg = null;
                var firstError = errors.ValueKind == JsonValueKind.Array
                    ? errors.EnumerateArray().FirstOrDefault()
                    : default;

                if (firstError.ValueKind == JsonValueKind.Object
                    && firstError.TryGetProperty("message", out var msgProp))
                {
                    errorMsg = msgProp.GetString();
                }

                if (errorMsg is null)
                {
                    // Unexpected error shape: the raw payload can echo back the
                    // failed mutation (issue title/description). Log only the
                    // length + a short fingerprint so support can correlate
                    // reports without spilling user content into traces.
                    var raw = errors.GetRawText();
                    var fingerprint = ShortFingerprint(raw);
                    Host?.Log(
                        PluginLogLevel.Error,
                        $"Linear GraphQL error: {{redacted:length={raw.Length}, sha256:{fingerprint}}}"
                    );
                }
                else
                {
                    Host?.Log(PluginLogLevel.Error, $"Linear GraphQL error: {errorMsg}");
                }

                return null;
            }

            // Clone so the returned element survives the JsonDocument's pooled-buffer disposal.
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // A 200 response with non-JSON body shouldn't crash settings validation;
            // log the parse failure plus a short fingerprint (not the raw body —
            // it may echo user content), then return null so callers recover.
            var fingerprint = ShortFingerprint(responseJson);
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API returned non-JSON body ({ex.Message}). Body length={responseJson.Length}, sha256:{fingerprint}"
            );
            return null;
        }
    }

    private static string ShortFingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string ExtractTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Untitled Issue";

        var firstLine = input
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
            return "Untitled Issue";

        return firstLine.Length > 100 ? firstLine[..100] : firstLine;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                null,
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "default-team-id",
                Loc.L("Settings.DefaultTeamId"),
                Description: _cachedTeams.Count > 0
                    ? Loc.L("Settings.CachedTeamsDescription", _cachedTeams.Count)
                    : Loc.L("Settings.DefaultTeamIdDescription"),
                Options: _cachedTeams.Count > 0
                    ? _cachedTeams
                        .Select(t => new PluginSettingOption(t.Id, $"{t.Key} - {t.Name}"))
                        .ToList()
                    : null
            ),
            new(
                "default-project-id",
                Loc.L("Settings.DefaultProjectId"),
                false,
                null,
                Loc.L("Settings.DefaultProjectIdDescription")
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "default-team-id" => DefaultTeamId,
                "default-project-id" => DefaultProjectId,
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
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var teams = await FetchTeamsAsync(ct);
        if (teams.Count == 0)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.NoTeamsFound"));

        return new PluginSettingsValidationResult(
            true,
            Loc.L("Settings.TeamsRefreshed", teams.Count)
        );
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
