using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Plugin.Deepgram;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DeepgramPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<DeepgramPlugin>();

    [Fact]
    public async Task SupportedLanguages_FollowTheSelectedModel()
    {
        using var deepgram = new DeepgramPlugin();
        await deepgram.ActivateAsync(new Mock<IPluginHostServices>().Object);

        Assert.Equal("nova-3", deepgram.SelectedModelId);
        Assert.Contains("en", deepgram.SupportedLanguages);
        Assert.Contains("de", deepgram.SupportedLanguages);
        Assert.Contains("ar", deepgram.SupportedLanguages);

        deepgram.SelectModel("nova-2");

        Assert.Contains("en", deepgram.SupportedLanguages);
        Assert.Contains("de-CH", deepgram.SupportedLanguages);
        Assert.DoesNotContain("ar", deepgram.SupportedLanguages);

        foreach (var model in deepgram.TranscriptionModels)
        {
            deepgram.SelectModel(model.Id);
            Assert.InRange(model.LanguageCount, 1, deepgram.SupportedLanguages.Count);
        }
    }

    [Fact]
    public async Task SelectModel_NotifiesCapabilitiesChangedOnlyOnChange()
    {
        var host = new Mock<IPluginHostServices>();
        using var deepgram = new DeepgramPlugin();
        await deepgram.ActivateAsync(host.Object);
        host.Invocations.Clear();

        deepgram.SelectModel("nova-2");
        deepgram.SelectModel("nova-2");

        host.Verify(h => h.SetSetting("selectedModel", "nova-2"), Times.Once);
        host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Once);
    }

    [Fact]
    public void ExplicitLanguage_IsValidatedBeforeUpload()
    {
        using var deepgram = new DeepgramPlugin();

        Assert.Throws<TranscriptionLanguageNotSupportedException>(() =>
            deepgram.ToLegacyLanguage(LanguageSelection.Explicit("xx")));
        Assert.Equal("de-CH", deepgram.ToLegacyLanguage(LanguageSelection.Explicit("de-CH")));
        Assert.Null(deepgram.ToLegacyLanguage(LanguageSelection.Automatic));
    }
}
