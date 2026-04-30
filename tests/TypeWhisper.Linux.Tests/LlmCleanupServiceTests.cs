using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LlmCleanupServiceTests
{
    [Fact]
    public async Task CleanAsync_Light_UsesDeterministicCleanup()
    {
        var sut = CreateService([]);

        var result = await sut.CleanAsync("um hello", CleanupLevel.Light);

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task CleanAsync_Medium_UsesConfiguredLlmPrompt()
    {
        var provider = new FakeLlmProviderPlugin("polished text");
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync("um hello", CleanupLevel.Medium);

        Assert.Equal("polished text", result);
        Assert.Equal(CleanupService.MediumSystemPrompt, provider.LastSystemPrompt);
        Assert.Equal("Hello", provider.LastUserText);
    }

    [Fact]
    public async Task CleanAsync_High_UsesConfiguredLlmPrompt()
    {
        var provider = new FakeLlmProviderPlugin("concise text");
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync("uh hello there", CleanupLevel.High);

        Assert.Equal("concise text", result);
        Assert.Equal(CleanupService.HighSystemPrompt, provider.LastSystemPrompt);
        Assert.Equal("Hello there", provider.LastUserText);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenNoProviderAvailable()
    {
        var statuses = new List<string>();
        var sut = CreateService([]);

        var result = await sut.CleanAsync(
            "um hello",
            CleanupLevel.Medium,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            });

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup provider unavailable. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenProviderFails()
    {
        var statuses = new List<string>();
        var provider = new FakeLlmProviderPlugin("unused") { ThrowOnProcess = true };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "um hello",
            CleanupLevel.Medium,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            });

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup failed. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenUnavailableStatusCallbackFails()
    {
        var sut = CreateService([]);

        var result = await sut.CleanAsync(
            "um hello",
            CleanupLevel.Medium,
            _ => throw new InvalidOperationException("Status failed."));

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenFailureStatusCallbackFails()
    {
        var provider = new FakeLlmProviderPlugin("unused") { ThrowOnProcess = true };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "um hello",
            CleanupLevel.Medium,
            _ => throw new InvalidOperationException("Status failed."));

        Assert.Equal("Hello", result);
    }

    private static LlmCleanupService CreateService(IReadOnlyList<ILlmProviderPlugin> providers)
    {
        var pluginManager = TestPluginManagerFactory.Create(llmProviders: providers);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var promptProcessing = new PromptProcessingService(pluginManager, settings.Object, new MemoryService(pluginManager));
        return new LlmCleanupService(new CleanupService(), promptProcessing);
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        private readonly string _result;

        public FakeLlmProviderPlugin(string result)
        {
            _result = result;
            SupportedModels = [new PluginModelInfo("model-a", "Model A")];
        }

        public string PluginId => "com.test.cleanup";
        public string PluginName => "Cleanup Provider";
        public string PluginVersion => "1.0.0";
        public string ProviderName => "Cleanup Provider";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserText { get; private set; }
        public bool ThrowOnProcess { get; init; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;

        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt;
            LastUserText = userText;

            if (ThrowOnProcess)
                throw new InvalidOperationException("Provider failed.");

            return Task.FromResult(_result);
        }

        public void Dispose() { }
    }
}
