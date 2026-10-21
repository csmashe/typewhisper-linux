using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using TypeWhisper.Plugin.GitHubCopilot;

namespace TypeWhisper.PluginSystem.Tests;

// Runs the real GitHub.Copilot.SDK against a local JSON-RPC peer, so the wire contract is
// checked without credentials, a downloaded runtime or paid requests.
public sealed class GitHubCopilotTransportTests
{
    private static readonly CopilotAccount s_personal = new("https://github.com", "personal");
    private static readonly CopilotAccount s_work = new("https://github.com", "work");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedModelSwitchSendsNoTextAndCleansUpTheSession(bool deferred)
    {
        // ReSharper disable once UsingStatementResourceInitialization, UsingStatementResourceInitializationExpression -- init-only flags cannot throw.
        await using var server = new FakeCopilotServer { DeferredSwitch = deferred, MismatchedModel = !deferred };
        await Assert.ThrowsAsync<CopilotModelUnavailableException>(() =>
            server.CreateTransport().ProcessAsync(s_personal, "s", "private", "model-a", CancellationToken.None));
        Assert.DoesNotContain(server.Requests, r => r.Method == "session.send");
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task DisabledModelFailsBeforeASessionIsCreated()
    {
        await using var server = new FakeCopilotServer();
        await Assert.ThrowsAsync<CopilotModelUnavailableException>(() =>
            server.CreateTransport().ProcessAsync(s_personal, "s", "private", "disabled", CancellationToken.None));
        Assert.DoesNotContain(server.Requests, r => r.Method is "session.create" or "session.send");
    }

    [Fact]
    public async Task TurnsReuseTheVerifiedCatalogUntilExpiryFailureOrInvalidation()
    {
        await using var server = new FakeCopilotServer();
        var clock = new ManualClock();
        var transport = server.CreateTransport(clock);
        await Turn();
        await Turn();
        Assert.Equal(1, server.Count("account.getAllUsers"));
        Assert.Equal(1, server.Count("models.list"));
        // The session's account binding is still verified on every turn.
        Assert.Equal(2, server.Count("session.gitHubAuth.getStatus"));

        clock.Now += CopilotTransport.CatalogLifetime;
        await Turn();
        Assert.Equal(2, server.Count("models.list"));

        server.MismatchedSessionAccount = true;
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(Turn);
        server.MismatchedSessionAccount = false;
        await Turn();
        Assert.Equal(3, server.Count("models.list"));

        transport.SetCatalogLifetime(TimeSpan.FromHours(2));
        await Turn();
        Assert.Equal(4, server.Count("models.list"));
        clock.Now += TimeSpan.FromMinutes(90);
        await Turn();
        Assert.Equal(4, server.Count("models.list"));
        return;

        Task<string> Turn() => transport.ProcessAsync(s_personal, "s", "u", "model-a", CancellationToken.None);
    }

    [Fact]
    public async Task SelectedAccountIsBoundWithoutTokensOrAGlobalLoginChange()
    {
        await using var server = new FakeCopilotServer();
        await server.CreateTransport().ProcessAsync(s_work, "s", "work text", "model-a", CancellationToken.None);
        Assert.Equal("opaque-work", server.Parameters("models.list").GetProperty("selectionId").GetString());
        var credentials = server.Parameters("session.gitHubAuth.setCredentials").GetProperty("credentials");
        Assert.Equal("user", credentials.GetProperty("type").GetString());
        Assert.Equal("work", credentials.GetProperty("login").GetString());
        Assert.False(credentials.TryGetProperty("token", out _));
        Assert.DoesNotContain(server.Requests, r =>
            r.Params.ValueKind != JsonValueKind.Undefined && r.Params.GetRawText().Contains("fixture-secret"));
        Assert.DoesNotContain(server.Requests, r => r.Method is "account.login" or "account.logout");
        var methods = server.Requests.Select(r => r.Method).ToList();
        Assert.True(methods.IndexOf("session.gitHubAuth.setCredentials") < methods.IndexOf("session.model.switchTo"));
        Assert.True(methods.IndexOf("session.gitHubAuth.getStatus") < methods.IndexOf("session.send"));
    }

    [Fact]
    public async Task AccountDiscoveryReturnsOnlyPublicIdentities()
    {
        await using var server = new FakeCopilotServer();
        var accounts = await server.CreateTransport().GetAccountsAsync(CancellationToken.None);
        Assert.Equal(["personal", "work"], accounts.Select(a => a.Login));
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(accounts));
        Assert.DoesNotContain(server.Requests, r => r.Method == "session.create");
    }

    [Fact]
    public async Task AMissingAccountStopsBeforeModelsOrASession()
    {
        await using var server = new FakeCopilotServer { MissingPersonal = true };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() =>
            server.CreateTransport().ProcessAsync(s_personal, "s", "private", "model-a", CancellationToken.None));
        Assert.DoesNotContain(server.Requests, r => r.Method is "models.list" or "session.create" or "session.send");
    }

    [Fact]
    public async Task AMismatchedSessionAccountNeverReceivesTheText()
    {
        await using var server = new FakeCopilotServer { MismatchedSessionAccount = true };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() =>
            server.CreateTransport().ProcessAsync(s_personal, "s", "private", "model-a", CancellationToken.None));
        Assert.DoesNotContain(server.Requests, r => r.Method is "session.send" or "session.model.switchTo");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task SendsOnlyTheTextWithEveryAgentFeatureDisabled()
    {
        await using var server = new FakeCopilotServer();
        var result = await server.CreateTransport()
            .ProcessAsync(s_personal, "Translate to German.", "Hello 世界\nline two", "model-a", CancellationToken.None);
        Assert.Equal("Hallo Welt", result);
        var create = server.Parameters("session.create");
        Assert.Equal("replace", create.GetProperty("systemMessage").GetProperty("mode").GetString());
        Assert.Equal("Translate to German.", create.GetProperty("systemMessage").GetProperty("content").GetString());
        Assert.Empty(create.GetProperty("availableTools").EnumerateArray());
        Assert.Equal(
            ["builtin:*", "custom:*", "mcp:*"],
            create.GetProperty("excludedTools").EnumerateArray().Select(v => v.GetString()));
        foreach (var flag in new[]
                 {
                     "enableConfigDiscovery", "enableOnDemandInstructionDiscovery", "enableSkills", "enableFileHooks",
                     "enableHostGitOperations", "enableFileChangeTracking", "enableSessionStore",
                     "enableSessionTelemetry",
                 })
        {
            Assert.False(create.GetProperty(flag).GetBoolean(), flag);
        }

        Assert.False(create.GetProperty("memory").GetProperty("enabled").GetBoolean());
        var send = server.Parameters("session.send");
        Assert.Equal("Hello 世界\nline two", send.GetProperty("prompt").GetString());
        Assert.False(send.TryGetProperty("attachments", out var attachments)
                     && attachments.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task CatalogFiltersDisabledModelsAndDuplicates()
    {
        await using var server = new FakeCopilotServer();
        var models = await server.CreateTransport().GetModelsAsync(s_personal, CancellationToken.None);
        Assert.Equal("model-a", Assert.Single(models).Id);
    }

    [Theory]
    [InlineData(false, "user")]
    [InlineData(true, "env")]
    [InlineData(true, "token")]
    public async Task SignedOutOrAmbientTokenAuthenticationIsRejected(bool authenticated, string authType)
    {
        await using var server = new FakeCopilotServer { Authenticated = authenticated, AuthType = authType };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() =>
            server.CreateTransport().GetModelsAsync(s_personal, CancellationToken.None));
        Assert.DoesNotContain(server.Requests, r => r.Method is "models.list" or "session.create");
    }

    [Fact]
    public async Task CancellationAbortsAndDeletesTheRemoteSession()
    {
        await using var server = new FakeCopilotServer { HoldResponse = true };
        using var cancel = new CancellationTokenSource();
        var task = server.CreateTransport().ProcessAsync(s_personal, "s", "u", "model-a", cancel.Token);
        // ReSharper disable MethodSupportsCancellation -- the timeouts are hang guards; the only token is cancelled on purpose.
        await server.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        // ReSharper restore MethodSupportsCancellation
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task SessionLogsWithTheUsersTextAreRemovedAfterEveryRequest()
    {
        await using var server = new FakeCopilotServer { ErrorResponse = true };
        var transport = server.CreateTransport();
        // The real runtime writes events.jsonl here and then cannot find the session to delete.
        var leftover = Path.Join(transport.SessionStateDirectory, "typewhisper-old");
        PrivateDirectory.Ensure(leftover);
        await File.WriteAllTextAsync(Path.Join(leftover, "events.jsonl"), "private dictation");
        server.OnSend = sessionId =>
        {
            var current = Path.Join(transport.SessionStateDirectory, sessionId);
            PrivateDirectory.Ensure(current);
            File.WriteAllText(Path.Join(current, "events.jsonl"), "private dictation");
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transport.ProcessAsync(s_personal, "s", "u", "model-a", CancellationToken.None));
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
        Assert.Empty(Directory.EnumerateFileSystemEntries(transport.SessionStateDirectory));
    }

    [Fact]
    public void RuntimeEnvironmentKeepsTheLoginAndProxyButNoTokensOrOverrides()
    {
        var source = new Dictionary<string, string>
        {
            ["HOME"] = "/home/fixture",
            ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
            ["https_proxy"] = "http://proxy.invalid:8080",
            ["NO_PROXY"] = "localhost",
            ["GH_TOKEN"] = "fixture-token",
            ["GITHUB_TOKEN"] = "fixture-token",
            ["COPILOT_GITHUB_TOKEN"] = "fixture-token",
            ["NODE_OPTIONS"] = "--require /tmp/inject.js",
            ["GH_CONFIG_DIR"] = "/home/fixture/.config/gh",
        };
        var root = Path.Join(Path.GetTempPath(), "copilot-env-" + Guid.NewGuid().ToString("N"));
        var transport = new CopilotTransport(root, () => "/runtime", readEnvironment: source.GetValueOrDefault);
        var options = transport.CreateClientOptions();
        var environment = options.Environment!;
        Assert.Equal("/home/fixture", environment["HOME"]);
        // A keyring-stored login is read over the desktop session bus.
        Assert.Equal("unix:path=/run/user/1000/bus", environment["DBUS_SESSION_BUS_ADDRESS"]);
        Assert.Equal("/run/user/1000", environment["XDG_RUNTIME_DIR"]);
        Assert.Equal("http://proxy.invalid:8080", environment["https_proxy"]);
        Assert.Equal("localhost", environment["NO_PROXY"]);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("TOKEN", StringComparison.Ordinal));
        Assert.DoesNotContain("NODE_OPTIONS", environment.Keys);
        // The gh CLI login would otherwise reach the SDK, which cannot parse it.
        Assert.Equal(Path.Join(root, "gh"), environment["GH_CONFIG_DIR"]);
        Assert.True(options.UseLoggedInUser);
        Assert.Null(options.GitHubToken);
        Assert.False(options.EnableRemoteSessions);
    }

    [Fact]
    public async Task EveryPermissionAndToolRequestIsDenied()
    {
        var transport = new CopilotTransport(Path.GetTempPath(), () => "/runtime");
        var config = transport.CreateSessionConfig("s", "m");
#pragma warning disable GHCP001
        var decision = await config.OnPermissionRequest!(null!, null!);
        Assert.IsType<GitHub.Copilot.Rpc.PermissionDecisionReject>(decision);
#pragma warning restore GHCP001
        var hook = await config.Hooks!.OnPreToolUse!(null!, null!);
        Assert.Equal("deny", hook!.PermissionDecision);
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>A local Content-Length framed JSON-RPC peer, adapted from upstream's transport tests.</summary>
internal sealed class FakeCopilotServer : IAsyncDisposable
{
    private static readonly string[] s_workOnly = ["work"];
    private static readonly string[] s_bothLogins = ["personal", "work"];
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _run;
    internal ConcurrentQueue<(string Method, JsonElement Params)> Requests { get; } = new();
    internal TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Authenticated { get; init; } = true;
    internal string AuthType { get; init; } = "user";
    internal bool HoldResponse { get; init; }
    internal bool ErrorResponse { get; init; }
    internal bool MismatchedSessionAccount { get; set; }
    internal bool MissingPersonal { get; init; }
    internal bool DeferredSwitch { get; init; }
    internal bool MismatchedModel { get; init; }
    internal Action<string>? OnSend { get; set; }
    private string _selectedLogin = "personal";

    internal FakeCopilotServer()
    {
        _listener.Start();
        _run = RunAsync();
    }

    private string Root { get; } = Path.Join(Path.GetTempPath(), "copilot-wire-" + Guid.NewGuid().ToString("N"));

    internal CopilotTransport CreateTransport(TimeProvider? time = null) => new(
        Root,
        () => "/unused",
        options =>
        {
            options.Connection = RuntimeConnection.ForUri(
                $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            // The fake peer handles authentication; production always uses its owned stdio runtime.
            options.UseLoggedInUser = null;
            return new CopilotClient(options);
        },
        time);

    internal JsonElement Parameters(string method) => Requests.Single(r => r.Method == method).Params;

    internal int Count(string method) => Requests.Count(r => r.Method == method);

    private async Task RunAsync()
    {
        try
        {
            // Each call owns and stops its runtime; serve those connections one after another.
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                try
                {
                    await ServeAsync(socket.GetStream());
                }
                catch (IOException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        await using (stream)
        {
            while (!_stop.IsCancellationRequested)
            {
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0)
                    {
                        return;
                    }

                    header.Append((char)one[0]);
                }

                var length = int.Parse(header.ToString().Split(':')[1].Trim());
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _stop.Token);
                using var json = JsonDocument.Parse(body);
                var request = json.RootElement;
                if (!request.TryGetProperty("method", out var methodElement))
                {
                    continue;
                }

                var method = methodElement.GetString()!;
                var parameters = request.TryGetProperty("params", out var p) ? p.Clone() : default;
                if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
                {
                    parameters = parameters[0];
                }

                Requests.Enqueue((method, parameters));
                if (!request.TryGetProperty("id", out var id))
                {
                    continue;
                }

                if (method == "session.gitHubAuth.setCredentials")
                {
                    _selectedLogin = parameters.GetProperty("credentials").GetProperty("login").GetString()!;
                }

                object result = method switch
                {
                    "connect" => new { protocolVersion = 3 },
                    "auth.getStatus" => new { isAuthenticated = Authenticated, authType = AuthType },
                    "account.getAllUsers" => !Authenticated
                        ? Array.Empty<object>()
                        : (MissingPersonal ? s_workOnly : s_bothLogins)
                        .Select(object (login) => new
                        {
                            authInfo = new
                            {
                                type = AuthType, host = "https://github.com", login,
                                token = "fixture-secret-never-forward", envVar = "GH_TOKEN",
                            },
                            selectionId = "opaque-" + login,
                            token = "fixture-secret-never-forward",
                        })
                        .ToArray(),
                    "session.gitHubAuth.getStatus" => new
                    {
                        isAuthenticated = true, authType = "user", host = "https://github.com",
                        login = MismatchedSessionAccount ? "different-account" : _selectedLogin,
                    },
                    "session.model.switchTo" => new
                    {
                        modelId = MismatchedModel ? "different-model" : parameters.GetProperty("modelId").GetString(),
                        deferred = DeferredSwitch,
                        status = "applied",
                    },
                    "models.list" => new
                    {
                        models = new object[]
                        {
                            new { id = "model-a", name = "Model A" },
                            new { id = "model-a", name = "Duplicate" },
                            new { id = "disabled", name = "Disabled", policy = new { state = "disabled" } },
                        },
                    },
                    "session.create" => new { sessionId = parameters.GetProperty("sessionId").GetString() },
                    "session.send" => new { messageId = "message-1" },
                    _ => new { success = true },
                };
                if (method == "session.send")
                {
                    OnSend?.Invoke(parameters.GetProperty("sessionId").GetString()!);
                }

                if (method == "session.send" && ErrorResponse)
                {
                    await WriteAsync(stream, new
                    {
                        jsonrpc = "2.0", id = id.Clone(),
                        error = new { code = -32000, message = "Fixture provider failure" },
                    });
                }
                else
                {
                    await WriteAsync(stream, new { jsonrpc = "2.0", id = id.Clone(), result });
                }

                if (method != "session.send")
                {
                    continue;
                }

                Sent.TrySetResult();
                if (HoldResponse || ErrorResponse)
                {
                    continue;
                }

                var sessionId = parameters.GetProperty("sessionId").GetString();
                await EventAsync(stream, sessionId, "assistant.message", new { content = "Hallo Welt", messageId = "answer-1" });
                await EventAsync(stream, sessionId, "session.idle", new { });
            }
        }
    }

    private Task EventAsync(NetworkStream stream, string? sessionId, string type, object data) => WriteAsync(
        stream,
        new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId,
                @event = new
                {
                    id = Guid.NewGuid().ToString(), timestamp = DateTimeOffset.UtcNow, parentId = (string?)null, type,
                    data,
                },
            },
        });

    private async Task WriteAsync(NetworkStream stream, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), _stop.Token);
        await stream.WriteAsync(body, _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _run;
        _stop.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }
}
