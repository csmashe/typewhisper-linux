using System.Text.Json;
using Moq;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranslationServiceTests
{
    [Fact]
    public async Task SegmentsUseJsonAndTargetPromptAndRecordProvenance()
    {
        var provider = CreateProvider();
        const string reply = "[{\"id\":0,\"text\":\"Hallo\"},{\"id\":1,\"text\":\"Welt\"}]";
        string? sentPrompt = null;
        string? sentText = null;
        provider.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<string>(), "test", It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((prompt, text, _, _) =>
            {
                sentPrompt = prompt;
                sentText = text;
            })
            .ReturnsAsync(reply);
        using var manager = TestPluginManagerFactory.Create(llmProviders: [provider.Object]);
        using var service = new TranslationService(manager);
        var capture = new LlmCallCapture();

        var result = await service.TranslateSegmentsAsync(["Hello", "World"], "en", "de", capture);

        Assert.Equal(["Hallo", "Welt"], result);
        Assert.Contains("into de.", sentPrompt);
        using var json = JsonDocument.Parse(sentText!);
        var entries = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].GetProperty("id").GetInt32());
        Assert.Equal("Hello", entries[0].GetProperty("text").GetString());
        Assert.Equal(1, entries[1].GetProperty("id").GetInt32());
        Assert.Equal("World", entries[1].GetProperty("text").GetString());
        var provenance = Assert.Single(capture.Calls);
        Assert.Equal(sentPrompt, provenance.SystemPromptSent);
        Assert.Equal(sentText, provenance.UserPromptSent);
        Assert.Equal(reply, provenance.ResponseReceived);
        Assert.Equal("test", provenance.ModelId);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("de", true)]
    public async Task SameLanguageOrEmptyInputReturnsInputWithoutProvider(string target, bool empty)
    {
        var provider = new Mock<ILlmProviderRole>(MockBehavior.Strict);
        using var manager = TestPluginManagerFactory.Create(llmProviders: [provider.Object]);
        using var service = new TranslationService(manager);
        IReadOnlyList<string> texts = empty ? [] : ["Hello", "World"];

        var result = await service.TranslateSegmentsAsync(texts, "en", target);

        Assert.Same(texts, result);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MismatchingReplySurfacesSegmentMismatch()
    {
        var provider = CreateProvider();
        provider.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<string>(), "test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("[]");
        using var manager = TestPluginManagerFactory.Create(llmProviders: [provider.Object]);
        using var service = new TranslationService(manager);

        await Assert.ThrowsAsync<SegmentTranslationMismatchException>(() =>
            service.TranslateSegmentsAsync(["Hello", "World"], "en", "de"));
    }

    private static Mock<ILlmProviderRole> CreateProvider()
    {
        var provider = new Mock<ILlmProviderRole>();
        provider.SetupGet(p => p.IsAvailable).Returns(true);
        provider.SetupGet(p => p.PluginId).Returns("test-provider");
        provider.SetupGet(p => p.ProviderName).Returns("Test provider");
        provider.SetupGet(p => p.SupportedModels).Returns([new PluginModelInfo("test", "Test")]);
        return provider;
    }
}
