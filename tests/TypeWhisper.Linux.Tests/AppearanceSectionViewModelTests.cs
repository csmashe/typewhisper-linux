using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppearanceSectionViewModelTests
{
    [Fact]
    public void Default_LoadsCurrentSettingAsSeconds()
    {
        var settings = CreateSettingsMock(AppSettings.Default);

        var sut = new AppearanceSectionViewModel(settings.Object);

        Assert.Equal(1.5, sut.PreviewBubbleAutoHideSeconds);
    }

    [Fact]
    public void SettingSeconds_PersistsNormalizedMilliseconds()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        var sut = new AppearanceSectionViewModel(settings.Object);

        sut.PreviewBubbleAutoHideSeconds = 3.75;

        settings.Verify(
            s => s.Save(It.Is<AppSettings>(a => a.PreviewBubbleAutoHideMilliseconds == 3750)),
            Times.Once);
    }

    [Fact]
    public void SettingSecondsAboveMax_ClampsToFiveSecondsOnPersist()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        var sut = new AppearanceSectionViewModel(settings.Object);

        sut.PreviewBubbleAutoHideSeconds = 7.0;

        settings.Verify(
            s => s.Save(It.Is<AppSettings>(a => a.PreviewBubbleAutoHideMilliseconds == 5000)),
            Times.Once);
    }

    private static Mock<ISettingsService> CreateSettingsMock(AppSettings current)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(current);
        settings
            .Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(saved => settings.SetupGet(s => s.Current).Returns(saved));
        return settings;
    }
}
