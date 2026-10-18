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

public sealed partial class LinearPlugin : IActionPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;

    // A redirected issueCreate would be replayed elsewhere (or dropped as GET), so none are followed.
    public LinearPlugin()
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }))
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
        RestoreCatalog(host);

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
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ActionResult(
                false,
                Loc.L("Settings.ApiKeyNotConfigured")
            );

        var teamId = DefaultTeamId;
        if (string.IsNullOrWhiteSpace(teamId))
            return new ActionResult(
                false,
                Loc.L("Settings.DefaultTeamNotConfigured")
            );

        var title = ExtractTitle(input);
        if (ct.IsCancellationRequested)
            return new ActionResult(false, Loc.L("Settings.IssueCreateCancelled"));

        // Never retried: Linear has no idempotency key, so a second attempt can create a duplicate.
        try
        {
            var created = await CreateIssueAsync(apiKey, teamId, DefaultProjectId, title, input, ct);

            return created.Outcome switch
            {
                IssueCreateOutcome.Created => new ActionResult(
                    true,
                    Loc.L("Settings.IssueCreated", title),
                    Url: created.Url,
                    DisplayDuration: 5.0
                ),
                IssueCreateOutcome.Uncertain => new ActionResult(
                    false,
                    Loc.L("Settings.IssueCreateUncertain"),
                    DisplayDuration: 10.0
                ),
                _ => new ActionResult(
                    false,
                    Loc.L("Settings.IssueCreateFailed")
                ),
            };
        }
        catch (OperationCanceledException)
        {
            // The request had started, so Linear may have created the issue anyway.
            return new ActionResult(
                false,
                Loc.L("Settings.IssueCreateCancelledUncertain"),
                DisplayDuration: 10.0
            );
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

    private async Task<IssueCreateResult> CreateIssueAsync(
        string apiKey,
        string teamId,
        string? projectId,
        string title,
        string description,
        CancellationToken ct
    )
    {
        var variables = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["teamId"] = teamId,
        };

        if (!string.IsNullOrWhiteSpace(projectId))
            variables["projectId"] = projectId;

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

        var response = await SendGraphQlAsync(apiKey, mutation, ct, variables);
        if (response.Outcome != GraphQlOutcome.Succeeded)
        {
            return new IssueCreateResult(
                response.Outcome == GraphQlOutcome.Uncertain ? IssueCreateOutcome.Uncertain : IssueCreateOutcome.Failed
            );
        }

        if (
            !response.Root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("issueCreate", out var issueCreate)
            || issueCreate.ValueKind != JsonValueKind.Object
            || !issueCreate.TryGetProperty("success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            // Linear processed the request but the reply does not say what happened.
            Host?.Log(PluginLogLevel.Warning, "Linear issueCreate response was incomplete; the issue may exist.");
            return new IssueCreateResult(IssueCreateOutcome.Uncertain);
        }

        if (!success.GetBoolean())
        {
            Host?.Log(
                PluginLogLevel.Warning,
                "Linear API returned success=false for issueCreate"
            );
            return new IssueCreateResult(IssueCreateOutcome.Failed);
        }

        string? url = null;
        string? identifier = null;
        if (issueCreate.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object)
        {
            url = ReadString(issue, "url");
            identifier = ReadString(issue, "identifier");
        }

        var issueUrl = IsLinearIssueUrl(url) ? url : null;
        if (issueUrl is null)
            Host?.Log(PluginLogLevel.Warning, "Linear created the issue but returned no linear.app link.");

        Host?.Log(PluginLogLevel.Info, $"Created Linear issue {identifier}");
        return new IssueCreateResult(IssueCreateOutcome.Created, issueUrl);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The host offers the link to open, so only Linear's own HTTPS pages are passed on.
    private static bool IsLinearIssueUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 }
        && uri.Host.Equals("linear.app", StringComparison.OrdinalIgnoreCase);

    private async Task<GraphQlResponse> SendGraphQlAsync(
        string apiKey,
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
        // Linear refuses a personal API key sent as a Bearer token (HTTP 400); only OAuth tokens use Bearer.
        if (apiKey.StartsWith("lin_oauth_", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);

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
            // TaskCanceledException; the request may have been processed.
            var fingerprint = ShortFingerprint(ex.ToString());
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API request timed out (sha256:{fingerprint})"
            );
            return GraphQlResponse.Uncertain;
        }
        catch (HttpRequestException ex)
        {
            var fingerprint = ShortFingerprint(ex.ToString());
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API transport error: {ex.Message} (sha256:{fingerprint})"
            );
            // Only a failure to reach Linear proves the request was never processed.
            return ex.HttpRequestError
                is HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError
                ? GraphQlResponse.Failed
                : GraphQlResponse.Uncertain;
        }

        using var responseScope = response;

        if (!response.IsSuccessStatusCode)
        {
            // Linear error bodies can echo back the failed mutation, including
            // issue titles/descriptions — log only status + a short fingerprint
            // of the response so the body is correlatable across reports
            // without leaking user content to the trace.
            var status = (int)response.StatusCode;
            // A server error can follow a completed write; redirects, client errors and 429 were not processed.
            var outcome = status >= 500 ? GraphQlResponse.Uncertain : GraphQlResponse.Failed;
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
                    $"Linear API error {status}; could not read body: {ex.Message} (sha256:{fp})"
                );
                return outcome;
            }
            var fingerprint = ShortFingerprint(errorBody);
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API error {status} (body length={errorBody.Length}, sha256:{fingerprint})"
            );
            return outcome;
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
            return GraphQlResponse.Uncertain;
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

                return IsRejectedBeforeExecution(doc.RootElement, errors)
                    ? GraphQlResponse.Failed
                    : GraphQlResponse.Uncertain;
            }

            // Clone so the returned element survives the JsonDocument's pooled-buffer disposal.
            return new GraphQlResponse(GraphQlOutcome.Succeeded, doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            // A 200 response with non-JSON body shouldn't crash settings validation;
            // log the parse failure plus a short fingerprint (not the raw body —
            // it may echo user content), then report it as unconfirmed.
            var fingerprint = ShortFingerprint(responseJson);
            Host?.Log(
                PluginLogLevel.Error,
                $"Linear API returned non-JSON body ({ex.Message}). Body length={responseJson.Length}, sha256:{fingerprint}"
            );
            return GraphQlResponse.Uncertain;
        }
    }

    // Linear error types that refuse a request before it runs; anything else may follow a write.
    private static readonly HashSet<string> s_rejectionErrorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "authentication error",
        "forbidden",
        "invalid input",
        "graphql error",
        "ratelimited",
        "usage limit exceeded",
        "featurenotaccessible",
    };

    private static bool IsRejectedBeforeExecution(JsonElement root, JsonElement errors)
    {
        // GraphQL omits "data" only when the request failed before execution started.
        if (!root.TryGetProperty("data", out _))
            return true;

        if (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            return false;

        foreach (var error in errors.EnumerateArray())
        {
            if (
                error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("extensions", out var extensions)
                || extensions.ValueKind != JsonValueKind.Object
                || ReadString(extensions, "type") is not { } type
                || !s_rejectionErrorTypes.Contains(type)
            )
            {
                return false;
            }
        }

        return true;
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
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
            return "Untitled Issue";

        if (firstLine.Length <= 100)
            return firstLine;

        // Cutting between a surrogate pair would send Linear an invalid character.
        return char.IsHighSurrogate(firstLine[99]) ? firstLine[..99] : firstLine[..100];
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
    {
        var catalog = CurrentCatalog;
        return
        [
            new PluginSettingDefinition(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                null,
                Loc.L("Settings.ApiKeyDescription")
            ),
            new PluginSettingDefinition(
                "default-team-id",
                Loc.L("Settings.DefaultTeamId"),
                Description: catalog is { Teams.Count: > 0 }
                    ? Loc.L("Settings.CachedTeamsDescription", catalog.Teams.Count)
                    : Loc.L("Settings.DefaultTeamIdDescription"),
                Options: catalog is { Teams.Count: > 0 }
                    ? catalog.Teams
                        .Select(t => new PluginSettingOption(t.Id, $"{t.Key} - {t.Name}"))
                        .ToList()
                    : null
            ),
            new PluginSettingDefinition(
                "default-project-id",
                Loc.L("Settings.DefaultProjectId"),
                Description: catalog is { Projects.Count: > 0 }
                    ? Loc.L("Settings.CachedProjectsDescription", catalog.Projects.Count)
                    : Loc.L("Settings.DefaultProjectIdDescription"),
                Options: catalog is { Projects.Count: > 0 }
                    ? catalog.Projects
                        .Select(p => new PluginSettingOption(p.Id, p.Name))
                        .Prepend(new PluginSettingOption("", Loc.L("Settings.NoProject")))
                        .ToList()
                    : null
            ),
        ];
    }

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

        var catalog = await RefreshCatalogAsync(ct);
        if (catalog is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.RefreshFailed"));

        if (catalog.Teams.Count == 0)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.NoTeamsFound"));

        return new PluginSettingsValidationResult(
            true,
            Loc.L("Settings.TeamsRefreshed", catalog.Teams.Count, catalog.Projects.Count)
        );
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal enum GraphQlOutcome
{
    Succeeded,
    // Linear did not process the request (unreachable, refused or rejected).
    Failed,
    // Linear may have processed the request; a write may have happened.
    Uncertain,
}

internal readonly record struct GraphQlResponse(GraphQlOutcome Outcome, JsonElement Root)
{
    public static GraphQlResponse Failed => new(GraphQlOutcome.Failed, default);
    public static GraphQlResponse Uncertain => new(GraphQlOutcome.Uncertain, default);
}

internal enum IssueCreateOutcome
{
    Created,
    Failed,
    Uncertain,
}

internal readonly record struct IssueCreateResult(IssueCreateOutcome Outcome, string? Url = null);
