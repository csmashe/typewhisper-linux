using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class PluginRegistryServiceTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private PluginManager? _manager;

    public PluginRegistryServiceTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(_loader, _eventBus, _activeWindow.Object, _profiles.Object, _settings.Object);
        return _manager;
    }

    private static HttpClient CreateMockHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
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
                RequiresApiKey = false
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
                RequiresApiKey = false
            }
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

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
                RequiresApiKey = false
            }
        };

        var json = JsonSerializer.Serialize(plugins);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            });

        var httpClient = new HttpClient(handler.Object);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FetchRegistryAsync();
        await service.FetchRegistryAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_SetsFlag()
    {
        AppSettings? savedSettings = null;
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = false });

        var httpClient = CreateMockHttpClient("[]");
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

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
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FirstRunAutoInstallAsync();

        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}
