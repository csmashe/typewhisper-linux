using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PluginRegistryServiceTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader;
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly string _tempRoot;
    private PluginManager? _manager;

    public PluginRegistryServiceTests()
    {
        _tempRoot = TestPaths.CreateTempDirectory(
            "TypeWhisper.Linux.PluginRegistryServiceTests"
        );
        _loader = new PluginLoader(Path.Join(_tempRoot, "PluginData"));
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    public void Dispose()
    {
        _manager?.Dispose();
        TestPaths.DeleteDirectory(_tempRoot);
    }

    [Fact]
    public async Task FetchRegistryAsync_DeserializesAndFiltersLinuxCompatiblePlugins()
    {
        var plugins = new[]
        {
            new
            {
                Id = "com.typewhisper.openai",
                Name = "OpenAI",
                Version = "1.0.0",
                Author = "Tester",
                Description = "A Linux-compatible plugin",
                Size = 1024L,
                DownloadUrl = "https://example.com/plugin.zip",
                Platform = "linux",
                Rid = "linux-x64",
                SdkAbi = "net10.0",
                RequiresApiKey = false,
            },
            new
            {
                Id = "com.typewhisper.live-transcript",
                Name = "Live Transcript",
                Version = "1.0.0",
                Author = "Tester",
                Description = "A Windows-only plugin entry for this test",
                Size = 1024L,
                DownloadUrl = "https://example.com/live-transcript.zip",
                Platform = "windows",
                Rid = "win-x64",
                SdkAbi = "net10.0",
                RequiresApiKey = false,
            },
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient)
        {
            RuntimeRid = "linux-x64",
        };

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("com.typewhisper.openai", result[0].Id);
    }

    [Fact]
    public async Task FetchRegistryAsync_CachesResults()
    {
        var plugins = new[]
        {
            new
            {
                Id = "com.typewhisper.openai",
                Name = "OpenAI",
                Version = "1.0.0",
                Author = "A",
                Description = "D",
                Size = 100L,
                DownloadUrl = "u",
                Platform = "linux",
                Rid = "linux-x64",
                SdkAbi = "net10.0",
                RequiresApiKey = false,
            },
        };

        var json = JsonSerializer.Serialize(plugins);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json),
                };
            });

        var httpClient = new HttpClient(handler.Object);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient)
        {
            RuntimeRid = "linux-x64",
        };

        await service.FetchRegistryAsync();
        await service.FetchRegistryAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_SetsFlag()
    {
        AppSettings? savedSettings = null;
        _settings
            .Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings { PluginFirstRunCompleted = false });

        var httpClient = CreateMockHttpClient("[]");
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient)
        {
            RuntimeRid = "linux-x64",
        };

        await service.FirstRunAutoInstallAsync();

        Assert.NotNull(savedSettings);
        Assert.True(savedSettings!.PluginFirstRunCompleted);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_SkipsWhenAlreadyCompleted()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = true });

        var httpClient = CreateMockHttpClient("[]");
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient)
        {
            RuntimeRid = "linux-x64",
        };

        await service.FirstRunAutoInstallAsync();

        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_OfflineFetch_DoesNotCompleteAndRetries()
    {
        AppSettings? savedSettings = null;
        _settings
            .Setup(settings => settings.Current)
            .Returns(new AppSettings { PluginFirstRunCompleted = false });
        _settings
            .Setup(settings => settings.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(settings => savedSettings = settings);
        var requests = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                requests++;
                return requests == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]"),
                    };
            });
        var service = new PluginRegistryService(
            CreateManager(),
            _loader,
            _settings.Object,
            new HttpClient(handler.Object)
        )
        {
            RuntimeRid = "linux-x64",
        };

        await service.FirstRunAutoInstallAsync();
        Assert.Null(savedSettings);

        await service.FirstRunAutoInstallAsync();

        Assert.Equal(2, requests);
        Assert.NotNull(savedSettings);
        Assert.True(savedSettings!.PluginFirstRunCompleted);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_OversizedRegistry_DoesNotComplete()
    {
        _settings
            .Setup(settings => settings.Current)
            .Returns(new AppSettings { PluginFirstRunCompleted = false });

        // Fails closed on the declared length alone, so the body stays small rather than
        // transferring the 8 MB it stands in for.
        var content = new StringContent("[]");
        content.Headers.ContentLength = 8L * 1024 * 1024 + 1;
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var service = new PluginRegistryService(
            CreateManager(),
            _loader,
            _settings.Object,
            new HttpClient(handler.Object)
        )
        {
            RuntimeRid = "linux-x64",
        };

        var result = await service.FetchRegistryAsync();
        await service.FirstRunAutoInstallAsync();

        Assert.Empty(result);
        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_TooManyRegistryEntries_DoesNotComplete()
    {
        _settings
            .Setup(settings => settings.Current)
            .Returns(new AppSettings { PluginFirstRunCompleted = false });

        var json = "[" + string.Join(",", Enumerable.Repeat("{}", 1025)) + "]";
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            );

        var service = new PluginRegistryService(
            CreateManager(),
            _loader,
            _settings.Object,
            new HttpClient(handler.Object)
        )
        {
            RuntimeRid = "linux-x64",
        };

        var result = await service.FetchRegistryAsync();
        await service.FirstRunAutoInstallAsync();

        // A few KB of "{}" sits under the byte ceiling but past the entry cap — the amplification
        // a byte limit on its own does not catch.
        Assert.True(json.Length < 8L * 1024 * 1024);
        Assert.Empty(result);
        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Theory]
    // A null entry is malformed, not "zero plugins": it must retry rather than complete first run.
    [InlineData("[null]")]
    [InlineData("[null,null,null]")]
    public async Task FirstRunAutoInstallAsync_NullRegistryEntry_DoesNotComplete(string json)
    {
        _settings
            .Setup(settings => settings.Current)
            .Returns(new AppSettings { PluginFirstRunCompleted = false });

        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            );

        var service = new PluginRegistryService(
            CreateManager(),
            _loader,
            _settings.Object,
            new HttpClient(handler.Object)
        )
        {
            RuntimeRid = "linux-x64",
        };

        var result = await service.FetchRegistryAsync();
        await service.FirstRunAutoInstallAsync();

        Assert.Empty(result);
        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []
        );
        return _manager;
    }

    private static HttpClient CreateMockHttpClient(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK
    )
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(statusCode) { Content = new StringContent(responseJson) }
            );

        return new HttpClient(handler.Object);
    }
}
