using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Linear;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// The StubHttpMessageHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class LinearPluginTests
{
    private const string TeamA = "11111111-1111-1111-1111-111111111111";
    private const string TeamB = "22222222-2222-2222-2222-222222222222";
    private const string ProjectA = "33333333-3333-3333-3333-333333333333";
    private const string ProjectB = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public async Task ActivateAsync_RestoresCredentialsAndDefaults()
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["api-key"] = "linear-key",
            },
        };
        host.SetSetting("default-team-id", "team-123");
        host.SetSetting("default-project-id", "project-456");

        using var sut = new LinearPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("linear-key", sut.ApiKey);
        Assert.Equal("team-123", sut.DefaultTeamId);
        Assert.Equal("project-456", sut.DefaultProjectId);
    }

    [Fact]
    public async Task ActivateAsync_IgnoresUnscopedTeamCacheFromEarlierVersions()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "linear-key" } };
        host.SetSetting(
            "cached-teams",
            $$"""[{"id":"{{TeamA}}","name":"Engineering","key":"ENG"}]"""
        );

        using var sut = new LinearPlugin();
        await sut.ActivateAsync(host);

        Assert.Null(Definition(sut, "default-team-id").Options);
    }

    [Fact]
    public async Task ValidateAsync_LoadsEveryPageOfTeamsAndProjectsWithoutWriting()
    {
        var requests = new List<(string Collection, string? After)>();
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var (query, after) = await ReadQueryAsync(request, ct);
            Assert.DoesNotContain("mutation", query);
            Assert.Contains("first: 100", query);
            if (query.Contains("teams("))
            {
                requests.Add(("teams", after));
                return after is null
                    ? JsonResponse(Page("teams", [Team(TeamB, "Web", "WEB")], "cursor-2"))
                    : JsonResponse(Page("teams", [Team(TeamA, "API", "API"), Team(TeamB, "Web", "WEB")]));
            }

            requests.Add(("projects", after));
            return JsonResponse(Page("projects", [Project(ProjectB, "zeta"), Project(ProjectA, "Alpha")]));
        });
        var host = ConfiguredHost();
        using var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Localized found 2 teams and 2 projects.", result.Message);
        Assert.Equal([("teams", null), ("teams", "cursor-2"), ("projects", null)], requests);
        Assert.Equal(
            [(TeamA, "API - API"), (TeamB, "WEB - Web")],
            Definition(sut, "default-team-id").Options!.Select(o => (o.Value, o.Label))
        );
        Assert.Equal(
            [("", "Localized no project"), (ProjectA, "Alpha"), (ProjectB, "zeta")],
            Definition(sut, "default-project-id").Options!.Select(o => (o.Value, o.Label))
        );
        Assert.Equal("", host.GetSetting<string>("cached-teams"));
    }

    [Fact]
    public async Task Catalog_IsRestoredForTheSameKeyAndHiddenForAnotherKey()
    {
        var host = ConfiguredHost();
        using (var first = new LinearPlugin(new HttpClient(CatalogHandler())))
        {
            await first.ActivateAsync(host);
            Assert.True((await first.ValidateAsync())!.IsSuccess);
        }

        using var sut = new LinearPlugin(new HttpClient(CatalogHandler()));
        await sut.ActivateAsync(host);
        Assert.NotNull(Definition(sut, "default-team-id").Options);
        Assert.DoesNotContain("linear-key", host.GetSetting<string>("selection-catalog"));

        await sut.SetSettingValueAsync("api-key", "other-account-key");

        Assert.Null(Definition(sut, "default-team-id").Options);
        Assert.Null(Definition(sut, "default-project-id").Options);

        await sut.SetSettingValueAsync("api-key", "linear-key");

        Assert.NotNull(Definition(sut, "default-team-id").Options);
    }

    [Theory]
    [InlineData("""{ "keyFingerprint": "FP", "teams": null, "projects": [] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [], "projects": null }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [null], "projects": [] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [{ "id": null, "name": "API", "key": "API" }], "projects": [] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [], "projects": [{ "id": "x", "name": null }] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [{}], "projects": [] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [], "projects": [{ "name": "Alpha" }] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [{ "id": "11111111-1111-1111-1111-111111111111", "name": " ", "key": "API" }], "projects": [] }""")]
    [InlineData("""{ "keyFingerprint": "FP", "teams": [{ "id": "not-a-guid", "name": "API", "key": "API" }], "projects": [] }""")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task ActivateAsync_IgnoresAnUnusableCachedCatalog(string cachedTemplate)
    {
        var host = ConfiguredHost();
        using (var first = new LinearPlugin(new HttpClient(CatalogHandler())))
        {
            await first.ActivateAsync(host);
            await first.ValidateAsync();
        }

        using var saved = JsonDocument.Parse(host.GetSetting<string>("selection-catalog")!);
        var fingerprint = saved.RootElement.GetProperty("keyFingerprint").GetString()!;
        host.SetSetting("selection-catalog", cachedTemplate.Replace("FP", fingerprint));

        using var sut = new LinearPlugin(new HttpClient(CatalogHandler()));
        await sut.ActivateAsync(host);

        Assert.Null(Definition(sut, "default-team-id").Options);
        Assert.Null(Definition(sut, "default-project-id").Options);
    }

    [Theory]
    [InlineData("errors")]
    [InlineData("http500")]
    [InlineData("repeatedCursor")]
    [InlineData("invalidId")]
    [InlineData("missingPageInfo")]
    public async Task ValidateAsync_FailedRefreshKeepsThePreviousCatalog(string failure)
    {
        var fail = false;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- intended: the test flips the flag so the second refresh fails.
            if (!fail)
                return await CatalogHandler().Respond(request, ct);

            return failure switch
            {
                "errors" => JsonResponse("""{ "errors": [{ "message": "denied" }] }"""),
                "http500" => JsonResponse("{}", HttpStatusCode.InternalServerError),
                "repeatedCursor" => JsonResponse(Page("teams", [Team(TeamB, "New", "NEW")], "same")),
                "invalidId" => JsonResponse(Page("teams", [Team("not-a-guid", "New", "NEW")])),
                _ => JsonResponse("""{ "data": { "teams": { "nodes": [] } } }"""),
            };
        });
        var host = ConfiguredHost();
        using var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        var savedCatalog = host.GetSetting<string>("selection-catalog");

        fail = true;
        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Localized refresh failed.", result.Message);
        Assert.Equal(savedCatalog, host.GetSetting<string>("selection-catalog"));
        Assert.Equal(
            [TeamA],
            Definition(sut, "default-team-id").Options!.Select(o => o.Value)
        );
    }

    [Fact]
    public async Task ValidateAsync_WithNoTeamsReportsNoTeams()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var (query, _) = await ReadQueryAsync(request, ct);
            return JsonResponse(Page(query.Contains("teams(") ? "teams" : "projects", []));
        });
        using var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(ConfiguredHost());

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Localized no teams.", result.Message);
        Assert.Null(Definition(sut, "default-team-id").Options);
    }

    [Fact]
    public async Task ValidateAsync_DiscardsACatalogLoadedWithAReplacedKey()
    {
        LinearPlugin? plugin = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var (query, _) = await ReadQueryAsync(request, ct);
            if (!query.Contains("projects("))
                return JsonResponse(Page("teams", [Team(TeamA, "API", "API")]));

            // ReSharper disable once AccessToModifiedClosure -- intended: the plugin is assigned after the handler it is built with.
            await plugin!.SetSettingValueAsync("api-key", "other-account-key", ct);
            return JsonResponse(Page("projects", []));
        });
        var host = ConfiguredHost();
        using var sut = plugin = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Null(host.GetSetting<string>("selection-catalog"));
        Assert.Null(Definition(sut, "default-team-id").Options);
    }

    [Theory]
    [InlineData("lin_api_personal", "lin_api_personal")]
    [InlineData("lin_oauth_token", "Bearer lin_oauth_token")]
    public async Task Requests_SendPersonalKeysRawAndOAuthTokensAsBearer(string key, string expected)
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(expected, string.Join(",", request.Headers.GetValues("Authorization")));
            return Task.FromResult(JsonResponse(Page("teams", [])));
        });
        var host = ConfiguredHost();
        host.Secrets["api-key"] = key;
        using var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        await sut.ValidateAsync();

        Assert.True(handler.CallCount > 0);
    }

    [Fact]
    public async Task SetSettingValueAsync_PersistsApiKeyTeamAndProject()
    {
        var host = new TestPluginHostServices();
        using var sut = new LinearPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("api-key", " linear-key ");
        await sut.SetSettingValueAsync("default-team-id", " team-123 ");
        await sut.SetSettingValueAsync("default-project-id", " project-456 ");

        Assert.Equal("linear-key", host.Secrets["api-key"]);
        Assert.Equal("team-123", host.GetSetting<string>("default-team-id"));
        Assert.Equal("project-456", host.GetSetting<string>("default-project-id"));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ExecuteAsync_PostsGraphQlMutationWithBearerAuthAndParsesIssue()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.linear.app/graphql", request.RequestUri?.ToString());
            Assert.Equal("linear-key", string.Join(",", request.Headers.GetValues("Authorization")));
            Assert.Contains(
                request.Headers.Accept,
                value => value.MediaType == "application/json"
            );
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Contains("mutation IssueCreate", root.GetProperty("query").GetString());
            var variables = root.GetProperty("variables");
            Assert.Equal("First issue", variables.GetProperty("title").GetString());
            Assert.Equal(
                "First issue\nFull issue description",
                variables.GetProperty("description").GetString()
            );
            Assert.Equal("team-123", variables.GetProperty("teamId").GetString());
            Assert.Equal("project-456", variables.GetProperty("projectId").GetString());

            return JsonResponse(
                """
                {
                  "data": {
                    "issueCreate": {
                      "success": true,
                      "issue": {
                        "id": "issue-789",
                        "identifier": "ENG-42",
                        "url": "https://linear.app/acme/issue/ENG-42"
                      }
                    }
                  }
                }
                """
            );
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync(
            "First issue\nFull issue description",
            EmptyContext(),
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Equal("Localized Linear issue created: First issue", result.Message);
        Assert.Equal("https://linear.app/acme/issue/ENG-42", result.Url);
        Assert.Equal(5.0, result.DisplayDuration);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(200, """{ "errors": [{ "message": "mutation rejected" }] }""")]
    [InlineData(200, """{ "data": { "issueCreate": { "success": false } } }""")]
    [InlineData(200, """{ "data": null, "errors": [{ "message": "Entity not found", "extensions": { "type": "invalid input" } }] }""")]
    [InlineData(200, """{ "data": null, "errors": [{ "message": "no", "extensions": { "type": "forbidden" } }] }""")]
    [InlineData(400, """{ "errors": [{ "message": "bad input" }] }""")]
    [InlineData(401, """{ "errors": [{ "message": "unauthenticated" }] }""")]
    [InlineData(302, "")]
    [InlineData(429, """{ "errors": [{ "message": "ratelimited" }] }""")]
    public async Task ExecuteAsync_RejectedRequestReturnsLocalizedFailure(
        int statusCode,
        string responseBody
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(responseBody, (HttpStatusCode)statusCode))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync(
            "Issue title",
            EmptyContext(),
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Equal("Localized Linear issue creation failed.", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(200, """{ "data": {} }""")]
    [InlineData(200, """{ "data": { "issueCreate": { "success": "yes" } } }""")]
    [InlineData(200, "not json")]
    [InlineData(200, """{ "data": { "issueCreate": { "success": true } }, "errors": [{ "message": "partial" }] }""")]
    [InlineData(200, """{ "data": null, "errors": [{ "message": "boom" }] }""")]
    [InlineData(200, """{ "data": { "issueCreate": null }, "errors": [{ "message": "boom", "path": ["issueCreate", "issue"] }] }""")]
    [InlineData(200, """{ "data": null, "errors": [{ "message": "boom", "extensions": { "type": "internal error" } }] }""")]
    [InlineData(500, """{ "error": "internal" }""")]
    [InlineData(502, """{ "error": "upstream failure" }""")]
    public async Task ExecuteAsync_UnconfirmedResultSaysTheIssueMayExistAndIsNotRetried(
        int statusCode,
        string responseBody
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(responseBody, (HttpStatusCode)statusCode))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync("Issue title", EmptyContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Localized Linear issue may exist.", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutSaysTheIssueMayExistAndIsNotRetried()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("{}");
        });
        using var sut = await CreateConfiguredPluginAsync(
            handler,
            http => http.Timeout = TimeSpan.FromMilliseconds(50)
        );

        var result = await sut.ExecuteAsync("Issue title", EmptyContext(), CancellationToken.None);

        Assert.Equal("Localized Linear issue may exist.", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "Localized Linear issue creation failed.")]
    [InlineData(HttpRequestError.ConnectionError, "Localized Linear issue creation failed.")]
    [InlineData(HttpRequestError.ResponseEnded, "Localized Linear issue may exist.")]
    public async Task ExecuteAsync_TransportFailureIsUncertainOnlyAfterLinearWasReached(
        HttpRequestError error,
        string expectedMessage
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(error, "transport"))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync("Issue title", EmptyContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("https://evil.example/issue/ENG-42")]
    [InlineData("http://linear.app/acme/issue/ENG-42")]
    [InlineData("https://user@linear.app/acme/issue/ENG-42")]
    [InlineData("https://linear.app:8443/acme/issue/ENG-42")]
    [InlineData(null)]
    public async Task ExecuteAsync_CreatedIssueWithoutALinearLinkStillSucceeds(string? url)
    {
        var issue = url is null ? """{ "identifier": "ENG-42" }""" : $$"""{ "identifier": "ENG-42", "url": "{{url}}" }""";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""{ "data": { "issueCreate": { "success": true, "issue": {{issue}} } } }"""
        )));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync("Issue title", EmptyContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Url);
    }

    [Theory]
    [InlineData(" \r\n   A useful title  \nBody", "A useful title")]
    [InlineData(" \n \n", "Untitled Issue")]
    public async Task ExecuteAsync_TitleIsTheFirstNonBlankLine(string input, string expectedTitle)
    {
        string? sentTitle = null;
        string? sentDescription = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var variables = document.RootElement.GetProperty("variables");
            sentTitle = variables.GetProperty("title").GetString();
            sentDescription = variables.GetProperty("description").GetString();
            return JsonResponse(CreatedIssue);
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.ExecuteAsync(input, EmptyContext(), CancellationToken.None);

        Assert.Equal(expectedTitle, sentTitle);
        Assert.Equal(input, sentDescription);
    }

    [Fact]
    public async Task ExecuteAsync_LongTitleIsNotCutInsideASurrogatePair()
    {
        string? sentTitle = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sentTitle = document.RootElement.GetProperty("variables").GetProperty("title").GetString();
            return JsonResponse(CreatedIssue);
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.ExecuteAsync(new string('a', 99) + "\U0001F600 tail", EmptyContext(), CancellationToken.None);

        Assert.Equal(new string('a', 99), sentTitle);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledSendsNothing()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(CreatedIssue)));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ExecuteAsync("Issue title", EmptyContext(), new CancellationToken(true));

        Assert.Equal("Localized Linear issue creation was cancelled.", result.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationReturnsLocalizedCancelledResult()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("""{ "data": {} }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var execution = sut.ExecuteAsync(
            "Issue title",
            EmptyContext(),
            cancellation.Token
        );
        // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; the only in-scope token is cancellation.Token (the token under test), which the next line cancels, so forwarding it here would abort this wait instead of guarding it.
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var result = await execution;

        Assert.False(result.Success);
        Assert.Equal("Localized Linear issue cancelled after sending.", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(false, true, "Localized Linear API key is required.")]
    [InlineData(true, false, "Localized Linear default team is required.")]
    public async Task ExecuteAsync_WhenUnconfigured_UsesLocalizedMessage(
        bool hasApiKey,
        bool hasDefaultTeam,
        string expectedMessage
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "data": {} }"""))
        );
        var host = new TestPluginHostServices();
        if (hasApiKey)
            host.Secrets["api-key"] = "linear-key";
        if (hasDefaultTeam)
            host.SetSetting("default-team-id", "team-123");

        using var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        var result = await sut.ExecuteAsync(
            "Issue title",
            EmptyContext(),
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(0, handler.CallCount);
    }

    private const string CreatedIssue =
        """{ "data": { "issueCreate": { "success": true, "issue": { "url": "https://linear.app/acme/issue/ENG-1" } } } }""";

    private static async Task<LinearPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler,
        Action<HttpClient>? configure = null
    )
    {
        var http = new HttpClient(handler);
        configure?.Invoke(http);
        var sut = new LinearPlugin(http);
        await sut.ActivateAsync(ConfiguredHost());
        return sut;
    }

    private static TestPluginHostServices ConfiguredHost()
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["api-key"] = "linear-key",
            },
        };
        host.SetSetting("default-team-id", "team-123");
        host.SetSetting("default-project-id", "project-456");
        return host;
    }

    private static PluginSettingDefinition Definition(LinearPlugin plugin, string key) =>
        Assert.Single(plugin.GetSettingDefinitions(), definition => definition.Key == key);

    private static StubHttpMessageHandler CatalogHandler() =>
        new(async (request, ct) =>
        {
            var (query, _) = await ReadQueryAsync(request, ct);
            return JsonResponse(
                query.Contains("teams(")
                    ? Page("teams", [Team(TeamA, "API", "API")])
                    : Page("projects", [Project(ProjectA, "Alpha")])
            );
        });

    private static async Task<(string Query, string? After)> ReadQueryAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var after = root.TryGetProperty("variables", out var variables)
            && variables.TryGetProperty("after", out var cursor)
            && cursor.ValueKind == JsonValueKind.String
                ? cursor.GetString()
                : null;
        return (root.GetProperty("query").GetString()!, after);
    }

    private static object Team(string id, string name, string key) => new { id, name, key };

    private static object Project(string id, string name) => new { id, name };

    private static string Page(string collection, object[] nodes, string? nextCursor = null) =>
        JsonSerializer.Serialize(new
        {
            data = new Dictionary<string, object>
            {
                [collection] = new
                {
                    nodes,
                    pageInfo = new { hasNextPage = nextCursor is not null, endCursor = nextCursor },
                },
            },
        });

    private static ActionContext EmptyContext() => new(null, null, null, null, null);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct) =>
            responder(request, ct);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _callCount);
            return responder(request, cancellationToken);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];

        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(s_jsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public void Log(PluginLogLevel level, string message)
        {
        }

        public void NotifyCapabilitiesChanged()
        {
            NotifyCapabilitiesChangedCount++;
        }
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        private static readonly IReadOnlyDictionary<string, string> s_values =
            new Dictionary<string, string>
            {
                ["Settings.ApiKeyNotConfigured"] = "Localized Linear API key is required.",
                ["Settings.DefaultTeamNotConfigured"] =
                    "Localized Linear default team is required.",
                ["Settings.IssueCreated"] = "Localized Linear issue created: {0}",
                ["Settings.IssueCreateFailed"] =
                    "Localized Linear issue creation failed.",
                ["Settings.IssueCreateCancelled"] =
                    "Localized Linear issue creation was cancelled.",
                ["Settings.IssueCreateError"] = "Localized Linear issue creation error: {0}",
                ["Settings.IssueCreateUncertain"] = "Localized Linear issue may exist.",
                ["Settings.IssueCreateCancelledUncertain"] =
                    "Localized Linear issue cancelled after sending.",
                ["Settings.TeamsRefreshed"] = "Localized found {0} teams and {1} projects.",
                ["Settings.RefreshFailed"] = "Localized refresh failed.",
                ["Settings.NoTeamsFound"] = "Localized no teams.",
                ["Settings.NoProject"] = "Localized no project",
            };

        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key) => s_values.GetValueOrDefault(key, key);

        public string GetString(string key, params object[] args) =>
            string.Format(GetString(key), args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
