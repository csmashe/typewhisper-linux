using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moq;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("kid")]
    [InlineData("signature")]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("nonce")]
    [InlineData("exp")]
    public async Task IdentityValidation_UsesInjectedJwksAndRejectsInvalidTokens(string? fault)
    {
        using var rsa = RSA.Create(2048);
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://auth.openai.com/.well-known/jwks.json", request.RequestUri!.ToString());
            return Task.FromResult(JsonResponse(Jwks(rsa)));
        }));
        var token = SignedIdentity(rsa, "nonce", fault);
        if (fault is null)
        {
            var identity = await OpenAiOAuthClient.ValidateIdTokenAsync(client, token,
                ChatGptIssuedClientId, "nonce", CancellationToken.None);
            Assert.Equal("fixture-user", identity.Subject);
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OpenAiOAuthClient.ValidateIdTokenAsync(client, token,
                    ChatGptIssuedClientId, "nonce", CancellationToken.None));
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("json")]
    [InlineData("keys")]
    [InlineData("rsa")]
    [InlineData("timeout")]
    public async Task IdentityValidation_WrapsMalformedIdentityAndJwksFailures(string fault)
    {
        using var rsa = RSA.Create(2048);
        using var client = new HttpClient(new CapturingHandler((_, _) => fault switch
        {
            "timeout" => throw new TaskCanceledException("fixture timeout"),
            "keys" => Task.FromResult(JsonResponse("{}")),
            "rsa" => Task.FromResult(JsonResponse("""{"keys":[{"kid":"fixture","kty":"RSA","n":"!","e":"AQAB"}]}""")),
            _ => Task.FromResult(JsonResponse(Jwks(rsa))),
        }));
        var token = fault switch
        {
            "base64" => "!.e30.AA",
            "json" => "bm90LWpzb24.e30.AA",
            _ => SignedIdentity(rsa, "nonce"),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiOAuthClient.ValidateIdTokenAsync(client, token, ChatGptIssuedClientId, "nonce", CancellationToken.None));
        Assert.NotNull(ex.InnerException);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(ChatGptGrantedScopes, true)]
    [InlineData("openid", false)]
    [InlineData("", false)]
    public async Task BrowserSignIn_FakeCallbackThroughStoredGrantAndInference(string? scope, bool permitted)
    {
        using var rsa = RSA.Create(2048);
        Uri? authorization = null;
        var paths = new List<string>();
        var server = new FakeOAuthCallbackServer(() => Task.FromResult(OpenAiLoopbackOAuthServer.ParseAuthorizationCallback(
                $"GET /auth/callback?code=fixture-code&client_id={ChatGptIssuedClientId}&state={ParseQuery(authorization!)["state"]} HTTP/1.1",
                ParseQuery(authorization!)["state"])));
        var processes = new Mock<IPluginProcessSupervisor>(MockBehavior.Strict);
        processes.Setup(p => p.LaunchUri(It.IsAny<Uri>())).Callback<Uri>(uri => authorization = uri)
            .Returns(new DetachedLaunchOutcome(true, null));
        using var client = new HttpClient(new CapturingHandler((request, body) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/accounts/oauth/token":
                    Assert.Contains("client_id=" + ChatGptIssuedClientId, body!);
                    return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                    {
                        access_token = "fixture-access", refresh_token = "fixture-refresh", expires_in = 3600,
                        id_token = SignedIdentity(rsa, ParseQuery(authorization!)["nonce"]), scope,
                    }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })));
                case "/.well-known/jwks.json":
                    return Task.FromResult(JsonResponse(Jwks(rsa)));
                case "/v1/models":
                    return Task.FromResult(JsonResponse("""{"data":[{"id":"gpt-fixture"}]}"""));
                case "/v1/responses":
                    Assert.Equal("Bearer fixture-access", request.Headers.Authorization!.ToString());
                    return Task.FromResult(JsonResponse("""{"status":"completed","output_text":"fixture result"}"""));
                default: throw new InvalidOperationException("Unexpected path " + request.RequestUri);
            }
        }));
        var host = new TestPluginHostServices { Processes = processes.Object };
        host.SetSetting("authMode", "chatgpt");
        using var sut = new OpenAiPlugin(client, oauthServerFactory: _ => server);
        await sut.ActivateAsync(host);
        var result = await sut.ValidateAsync();
        Assert.Equal(permitted, result!.IsSuccess);
        Assert.Equal(ChatGptIssuedClientId, host.GetSetting<string>("oauthClientId"));
        var hostId = host.GetSetting<string>("oauthHostId");
        Assert.False(string.IsNullOrWhiteSpace(hostId));
        if (permitted)
        {
            Assert.Equal(scope ?? ChatGptGrantedScopes, host.GetSetting<string>("oauthGrantedScopes"));
            Assert.True(sut.IsAvailable);
            Assert.Equal("fixture result", await sut.ProcessAsync("system", "input", "gpt-fixture", CancellationToken.None));
            Assert.Equal(["/api/accounts/oauth/token", "/.well-known/jwks.json", "/v1/models", "/v1/responses"], paths);
        }
        else Assert.False(sut.IsAvailable);
        await sut.ClearChatGptLoginAsync();
        Assert.Equal(ChatGptIssuedClientId, host.GetSetting<string>("oauthClientId"));
        Assert.Equal(hostId, host.GetSetting<string>("oauthHostId"));
        Assert.Empty(host.Secrets);
        Assert.False(sut.IsAvailable);
    }

    [Fact]
    public async Task BrowserSignIn_FailedExchangeKeepsRegistrationForRetry()
    {
        var authorizations = new List<Uri>();
        var processes = new Mock<IPluginProcessSupervisor>();
        processes.Setup(p => p.LaunchUri(It.IsAny<Uri>())).Callback<Uri>(authorizations.Add)
            .Returns(new DetachedLaunchOutcome(true, null));
        var server = new FakeOAuthCallbackServer(() => Task.FromResult(new OpenAiOAuthCallback("code", ChatGptIssuedClientId)));
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") })));
        var host = new TestPluginHostServices { Processes = processes.Object };
        host.SetSetting("authMode", "chatgpt");
        using var sut = new OpenAiPlugin(client, oauthServerFactory: _ => server);
        await sut.ActivateAsync(host);
        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        var hostId = host.GetSetting<string>("oauthHostId");
        Assert.Equal(ChatGptIssuedClientId, host.GetSetting<string>("oauthClientId"));
        await sut.ClearChatGptLoginAsync();
        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(ChatGptIssuedClientId, ParseQuery(authorizations[1])["client_id"]);
        Assert.Equal(hostId, ParseQuery(authorizations[1])["ext_agent_host_id"]);
        Assert.False(ParseQuery(authorizations[1]).ContainsKey("agent_name_hint"));
    }

    [Theory]
    [InlineData(null, ChatGptGrantedScopes)]
    [InlineData(ChatGptIssuedClientId, "openid")]
    [InlineData(ChatGptIssuedClientId, ChatGptGrantedScopes)]
    public async Task ChatGptAvailability_RequiresIssuedRegistrationAndPlanPermission(string? clientId, string scopes)
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthClientId", clientId);
        host.SetSetting("oauthGrantedScopes", scopes);
        host.Secrets["oauth-access-token"] = "fixture";
        using var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);
        Assert.Equal(clientId is not null && scopes == ChatGptGrantedScopes, sut.IsAvailable);
    }

    [Fact]
    public async Task CallbackListener_RequiresAdvertisedIpv4Port()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new OpenAiLoopbackOAuthServer("state", port);
        Assert.Throws<SocketException>(server.Start);
    }

    private static string Jwks(RSA rsa)
    {
        var key = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new { keys = new[] { new
        {
            kid = "fixture", kty = "RSA", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!),
        } } });
    }

    private static string SignedIdentity(RSA rsa, string nonce, string? fault = null)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = fault == "kid" ? "wrong" : "fixture" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = fault == "iss" ? "https://wrong.example" : OpenAiOAuthClient.Issuer,
            aud = fault == "aud" ? "wrong-client" : ChatGptIssuedClientId,
            nonce = fault == "nonce" ? "wrong-nonce" : nonce,
            exp = DateTimeOffset.UtcNow.AddHours(fault == "exp" ? -1 : 1).ToUnixTimeSeconds(), sub = "fixture-user",
        }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (fault == "signature") signature[0] ^= 0xff;
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FakeOAuthCallbackServer(Func<Task<OpenAiOAuthCallback>> callback) : IOpenAiOAuthCallbackServer
    {
        private bool _started;
        public void Start() => _started = true;
        public Task<OpenAiOAuthCallback> WaitForAuthorizationCallbackAsync(CancellationToken ct)
        {
            Assert.True(_started);
            ct.ThrowIfCancellationRequested();
            return callback();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
