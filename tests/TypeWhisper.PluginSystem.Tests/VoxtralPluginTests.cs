using Moq;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public class VoxtralPluginTests
{
    [Fact]
    public async Task ActivateAsync_UsesVoxtralMiniAndDisablesTranslation()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
        Assert.Equal(
            ["voxtral-mini-latest"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray()
        );
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacyModelSelection()
    {
        var host = CreateHostMock("mistral-whisper");

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
        host.Verify(
            service => service.SetSetting("selectedModel", "voxtral-mini-latest"),
            Times.Once
        );
    }

    [Fact]
    public async Task SelectModel_NormalizesLegacyModelId()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        sut.SelectModel("mistral-whisper");

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
    }

    private static Mock<IPluginHostServices> CreateHostMock(string? selectedModelId = null)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.LoadSecretAsync("api-key")).ReturnsAsync((string?)null);
        host.Setup(service => service.GetSetting<string>("selectedModel"))
            .Returns(selectedModelId);
        return host;
    }
}
