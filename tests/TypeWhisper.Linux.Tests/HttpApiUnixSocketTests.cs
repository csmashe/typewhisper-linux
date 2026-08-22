using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HttpApiUnixSocketTests
{
    private const string Token =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task TcpAndUnixEndpointsServeStatusWithBearer()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: true);
        using var uds = fixture.CreateUnixClient(withBearer: true);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        using var udsResponse = await uds.GetAsync("/v1/status");

        Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, udsResponse.StatusCode);
        using var tcpBody = JsonDocument.Parse(await tcpResponse.Content.ReadAsStringAsync());
        using var udsBody = JsonDocument.Parse(await udsResponse.Content.ReadAsStringAsync());
        Assert.Equal("1.0", tcpBody.RootElement.GetProperty("api_version").GetString());
        Assert.Equal("1.0", udsBody.RootElement.GetProperty("api_version").GetString());

        using var discovery = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixture.DiscoveryPath)
        );
        Assert.Equal(2, discovery.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(
            fixture.SocketPath,
            discovery.RootElement.GetProperty("socket_path").GetString()
        );
        Assert.Equal(Token, discovery.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void UnixSocketIsOwnerReadWriteOnly()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(fixture.SocketPath)
        );
#pragma warning restore CA1416
    }

    [Fact]
    public async Task UnixPeerMiddlewareRejectsMismatchedUid()
    {
        using var fixture = new ApiFixture(_ => false);
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: true);
        using var uds = fixture.CreateUnixClient(withBearer: true);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await uds.GetAsync("/v1/status")
        );
    }

    [Fact]
    public async Task BearerIsRequiredOnBothTransports()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: false);
        using var uds = fixture.CreateUnixClient(withBearer: false);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        using var udsResponse = await uds.GetAsync("/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, tcpResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, udsResponse.StatusCode);
        Assert.Equal("Bearer", tcpResponse.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("Bearer", udsResponse.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public void EmbeddedHostDoesNotTakeOverProcessSignals()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

        // ConsoleLifetime cancels SIGINT/SIGTERM and only stops this inner host,
        // which would leave the desktop app running after a logout or `kill`.
        Assert.IsNotType<ConsoleLifetime>(fixture.Service.HostLifetime);
    }

    [Fact]
    public async Task AmbientKestrelEndpointConfigurationIsIgnored()
    {
        var roguePort = ApiFixture.GetFreeTcpPort();
        Environment.SetEnvironmentVariable(
            "Kestrel__Endpoints__Rogue__Url",
            $"http://127.0.0.1:{roguePort}"
        );
        try
        {
            using var fixture = new ApiFixture();
            fixture.Start();

            using var tcp = fixture.CreateTcpClient(withBearer: true);
            using var tcpResponse = await tcp.GetAsync("/v1/status");
            Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);

            using var rogue = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            var refused = await Assert.ThrowsAsync<SocketException>(async () =>
                await rogue.ConnectAsync(IPAddress.Loopback, roguePort)
            );
            Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Rogue__Url", null);
        }
    }

    [Fact]
    public void UnpublishableDiscoveryFileIsReportedInStatus()
    {
        using var fixture = new ApiFixture();
        fixture.BlockDiscoveryDirectory();

        fixture.Service.ApplySettings();

        Assert.False(File.Exists(fixture.DiscoveryPath));
        Assert.Contains(
            "the CLI cannot connect",
            fixture.Service.StatusText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void StopDeletesDiscoveryAndUnixSocket()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        Assert.True(File.Exists(fixture.DiscoveryPath));
        Assert.True(File.Exists(fixture.SocketPath));

        fixture.Disable();

        Assert.False(File.Exists(fixture.DiscoveryPath));
        Assert.False(File.Exists(fixture.SocketPath));
        Assert.Equal("Local API is disabled.", fixture.Service.StatusText);
    }

    [Fact]
    public async Task ThirdConcurrentRequestGetsPinned429Response()
    {
        using var entered = new CountdownEvent(HttpApiService.MaxConcurrentRequests);
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally block awaits both in-flight requests, so the callback is done before these leave scope.
            .Returns(() =>
            {
                entered.Signal();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var first = client.GetAsync("/v1/history");
        var second = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            using var third = await client.GetAsync("/v1/status");
            var body = await third.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
            Assert.Equal("1", third.Headers.RetryAfter?.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(
                "Too many concurrent requests",
                json.RootElement.GetProperty("error").GetString()
            );
        }
        finally
        {
            release.Set();
            using var firstResponse = await first;
            using var secondResponse = await second;
        }
    }

    [Fact]
    public async Task Quiesce_WaitsForParkedRequest_RejectsNewConnections_ThenCompletes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally block releases and observes the parked request before the gates leave scope.
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var parked = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var quiesce = fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5));
            Assert.False(quiesce.IsCompleted);

            using var newClient = fixture.CreateTcpClient(withBearer: true);
            HttpResponseMessage? rejected = null;
            try
            {
                rejected = await newClient.GetAsync("/v1/status");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Null(rejected.Headers.RetryAfter);
            }
            catch (HttpRequestException)
            {
                // Kestrel may close the listener before the post-close request connects.
            }
            finally
            {
                rejected?.Dispose();
            }

            Assert.False(quiesce.IsCompleted);
            release.Set();
            Assert.True(await quiesce);
            await ObserveRequestCompletionAsync(parked);
            Assert.False(File.Exists(fixture.SocketPath));
            Assert.False(File.Exists(fixture.DiscoveryPath));
        }
        finally
        {
            release.Set();
            await ObserveRequestCompletionAsync(parked);
        }
    }

    [Fact]
    public async Task ApplySettings_AfterQuiesce_DoesNotRestartListener()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

        Assert.True(await fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5)));
        fixture.Service.ApplySettings();

        Assert.Null(fixture.Service.HostLifetime);
        Assert.False(File.Exists(fixture.SocketPath));
        Assert.False(File.Exists(fixture.DiscoveryPath));
        using var client = fixture.CreateTcpClient(withBearer: true);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync("/v1/status")
        );
    }

    [Fact]
    public async Task Quiesce_AfterTimedOutDrain_RedrainsCompletedHandler()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally releases and observes the request before the gates leave scope.
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var parked = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(await fixture.Service.QuiesceAsync(TimeSpan.Zero));

            release.Set();
            await ObserveRequestCompletionAsync(parked);

            Assert.True(await fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            await ObserveRequestCompletionAsync(parked);
        }
    }

    [Fact]
    public async Task LocalFileEndpointRejectsUnknownTaskAndFormat()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);

        var taskError = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"transalte"}"""
        );
        var formatError = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"response_format":"xml"}"""
        );

        Assert.Contains("Invalid task", taskError, StringComparison.Ordinal);
        Assert.Contains("Invalid response_format", formatError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalFileEndpointRejectsInvalidLanguageWithStableReason()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"language":"notalang"}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid_language_selection",
            json.RootElement.GetProperty("reason").GetString()
        );
        Assert.Contains(
            "valid BCP-47 tag",
            json.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task LocalFileEndpointRejectedFallbackProbeReturnsStableReason()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                1,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Unsupported format",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "unsupported_audio_format",
            json.RootElement.GetProperty("reason").GetString()
        );
        var invocation = Assert.Single(fixture.ProcessRunner.SupervisorInvocations);
        Assert.Equal("ffmpeg", invocation.Command.FileName);
        Assert.Equal(audioPath, invocation.Command.Arguments[4]);
    }

    [Fact]
    public async Task LocalFileEndpointProbeTimeoutReturnsServiceUnavailable()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.TimedOut,
                null,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Audio probe timed out",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "audio_probe_timeout",
            json.RootElement.GetProperty("reason").GetString()
        );
    }

    [Fact]
    public async Task LocalFileEndpointSuccessfulFallbackProbePassesFormatGate()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                0,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);

        var error = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"invalid"}"""
        );

        Assert.Contains("Invalid task", error, StringComparison.Ordinal);
        var invocation = Assert.Single(fixture.ProcessRunner.SupervisorInvocations);
        Assert.Equal("ffmpeg", invocation.Command.FileName);
    }

    [Fact]
    public async Task LocalFileEndpointRecognizedExtensionDoesNotProbe()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);

        var error = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"invalid"}"""
        );

        Assert.Contains("Invalid task", error, StringComparison.Ordinal);
        Assert.Empty(fixture.ProcessRunner.SupervisorInvocations);
    }

    [Fact]
    public async Task ProfileToggleApi_EnableWithCollidingHotkey_Returns409AndLeavesDisabled()
    {
        using var fixture = new ApiFixture();
        fixture.Profiles.AddProfile(
            new Profile
            {
                Id = "disabled-profile",
                Name = "Disabled profile",
                IsEnabled = false,
                HotkeyData = "Alt+F8",
            }
        );
        fixture.PromptActions.AddAction(
            new PromptAction
            {
                Id = "enabled-action",
                Name = "Enabled action",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
            }
        );
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);

        using var response = await client.PutAsync(
            "/v1/profiles/toggle?id=disabled-profile",
            null
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("hotkey-collision", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(
            "Profile hotkey cannot be enabled because it conflicts with an enabled shortcut.",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.False(Assert.Single(fixture.Profiles.Profiles).IsEnabled);
    }

    [Fact]
    public async Task ProfileToggleApi_DisableDoesNotRequireHotkeyValidation()
    {
        using var fixture = new ApiFixture();
        fixture.Profiles.AddProfile(
            new Profile
            {
                Id = "enabled-profile",
                Name = "Enabled profile",
                HotkeyData = "Ctrl+NoSuchKey",
                HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
                PromptActionId = "missing-action",
            }
        );
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);

        using var response = await client.PutAsync(
            "/v1/profiles/toggle?id=enabled-profile",
            null
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("is_enabled").GetBoolean());
        Assert.False(Assert.Single(fixture.Profiles.Profiles).IsEnabled);
    }

    private static async Task<string> PostLocalFileForErrorAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString()!;
    }

    private static async Task ObserveRequestCompletionAsync(
        Task<HttpResponseMessage> request
    )
    {
        try
        {
            using var response = await request;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Quiesce cancels the admitted request's linked RequestAborted token.
        }
    }

    private sealed class ApiFixture : IDisposable
    {
        private readonly string? _originalConfigHome =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _tempDirectory =
            TestPaths.CreateTempDirectory("TypeWhisper.HttpApiUnixSocketTests");
        private readonly HotkeyService _hotkeys = TestShortcutBackend.CreateHotkeyService();
        private readonly ModelManagerService _models;
        private readonly ProfileService _profiles;
        private readonly PromptActionService _promptActions;
        private readonly DictationSessionResultStore _sessionResults = new();
        private AppSettings _current;

        internal ApiFixture(
            Func<Socket, bool>? validateUnixPeer = null,
            Mock<IHistoryService>? history = null,
            ProcessRunOutcome? audioProbeResult = null
        )
        {
            Port = GetFreeTcpPort();
            SocketPath = Path.Join(_tempDirectory, "api.sock");
            DiscoveryPath = Path.Join(
                _tempDirectory,
                "config",
                "typewhisper",
                "api-discovery.json"
            );
            Environment.SetEnvironmentVariable(
                "XDG_CONFIG_HOME",
                Path.Join(_tempDirectory, "config")
            );

            _current = new AppSettings
            {
                ApiServerEnabled = true,
                ApiServerPort = Port,
                ApiServerBearerToken = Token,
            };
            Settings = new Mock<ISettingsService>();
            Settings.SetupGet(service => service.Current).Returns(() => _current);
            Settings
                .Setup(service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()))
                .Returns((Func<AppSettings, AppSettings> mutate) =>
                {
                    _current = mutate(_current);
                    return _current;
                });

            _models = new ModelManagerService(
                TestPluginManagerFactory.Create(),
                Settings.Object
            );
            var historyService = history ?? new Mock<IHistoryService>();
            _profiles = new ProfileService(Path.Join(_tempDirectory, "profiles.json"));
            _promptActions = new PromptActionService(
                Path.Join(_tempDirectory, "prompt-actions.json")
            );
            ProcessRunner = new FakeProcessRunner
            {
                SupervisorDefault = audioProbeResult,
            };
            var commands = new SystemCommandAvailabilityService(ProcessRunner);
            // The content probe short-circuits to "unsupported" when ffmpeg is
            // absent, so probe-path tests need it declared available.
            commands.RaiseSnapshotChangedForTests(
                commands.GetSnapshot() with { HasFfmpeg = true }
            );
            ProcessRunner.Invocations.Clear();
            ProcessRunner.SupervisorInvocations.Clear();
            var audioFiles = new AudioFileService(commands, ProcessRunner);
            Service = new HttpApiService(
                _models,
                Settings.Object,
                audioFiles,
                historyService.Object,
                _profiles,
                _promptActions,
                _hotkeys,
                null!,
                null!,
                null!,
                null!,
                null!,
                _sessionResults,
                new ApiDiscoveryFile(),
                Path.Join(_tempDirectory, "secret-protection.key"),
                SocketPath,
                validateUnixPeer
            );
        }

        private int Port { get; }

        internal string SocketPath { get; }

        internal string DiscoveryPath { get; }

        internal ProfileService Profiles => _profiles;

        internal PromptActionService PromptActions => _promptActions;

        internal FakeProcessRunner ProcessRunner { get; }

        private Mock<ISettingsService> Settings { get; }

        internal HttpApiService Service { get; }

        internal void Start()
        {
            Service.ApplySettings();
            Assert.StartsWith(
                "Local API is running",
                Service.StatusText,
                StringComparison.Ordinal
            );
        }

        /// <summary>Creates an empty file with a supported audio extension so the handler reaches option validation.</summary>
        internal string CreateSupportedAudioFile()
        {
            return CreateAudioFile("clip.wav");
        }

        internal string CreateAudioFile(string fileName)
        {
            var path = Path.Join(_tempDirectory, fileName);
            File.WriteAllBytes(path, []);
            return path;
        }

        /// <summary>Occupies the discovery directory's path with a file so the write fails.</summary>
        internal void BlockDiscoveryDirectory()
        {
            var directory = Path.GetDirectoryName(DiscoveryPath)!;
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            File.WriteAllText(directory, "not a directory");
        }

        internal void Disable()
        {
            _current = _current with { ApiServerEnabled = false };
            Service.ApplySettings();
        }

        internal HttpClient CreateTcpClient(bool withBearer)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{Port}"),
                Timeout = TimeSpan.FromSeconds(3),
            };
            AddBearer(client, withBearer);
            return client;
        }

        internal HttpClient CreateUnixClient(bool withBearer)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(
                        AddressFamily.Unix,
                        SocketType.Stream,
                        ProtocolType.Unspecified
                    );
                    try
                    {
                        await socket.ConnectAsync(
                            new UnixDomainSocketEndPoint(SocketPath),
                            ct
                        );
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost"),
                Timeout = TimeSpan.FromSeconds(3),
            };
            AddBearer(client, withBearer);
            return client;
        }

        public void Dispose()
        {
            Service.Dispose();
            _sessionResults.Dispose();
            _models.Dispose();
            _hotkeys.Dispose();
            Environment.SetEnvironmentVariable(
                "XDG_CONFIG_HOME",
                _originalConfigHome
            );
            TestPaths.DeleteDirectory(_tempDirectory);
        }

        private static void AddBearer(HttpClient client, bool withBearer)
        {
            if (withBearer)
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Token);
            }
        }

        internal static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
