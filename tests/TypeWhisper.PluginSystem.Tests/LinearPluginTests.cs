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
    [Fact]
    public async Task ActivateAsync_RestoresCredentialsDefaultsAndCachedTeams()
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
        host.SetSetting(
            "cached-teams",
            """[{"id":"team-123","name":"Engineering","key":"ENG"}]"""
        );

        using var sut = new LinearPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("linear-key", sut.ApiKey);
        Assert.Equal("team-123", sut.DefaultTeamId);
        Assert.Equal("project-456", sut.DefaultProjectId);
        var teamSetting = Assert.Single(
            sut.GetSettingDefinitions(),
            definition => definition.Key == "default-team-id"
        );
        var option = Assert.Single(teamSetting.Options!);
        Assert.Equal("team-123", option.Value);
        Assert.Equal("ENG - Engineering", option.Label);
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
            Assert.Equal("Bearer linear-key", request.Headers.Authorization?.ToString());
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
    [InlineData(200, """{ "data": {} }""")]
    [InlineData(200, """{ "errors": [{ "message": "mutation rejected" }] }""")]
    [InlineData(502, """{ "error": "upstream failure" }""")]
    public async Task ExecuteAsync_MalformedOrNonSuccessResponseReturnsLocalizedFailure(
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
        Assert.Equal("Localized Linear issue creation was cancelled.", result.Message);
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

    private static async Task<LinearPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler
    )
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

        var sut = new LinearPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

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
