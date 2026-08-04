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

        var sut = new AppearanceSectionViewModel(settings.Object, post: action => action());

        Assert.Equal(1.5, sut.PreviewBubbleAutoHideSeconds);
    }

    [Fact]
    public void SettingSeconds_PersistsNormalizedMilliseconds()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        _ = new AppearanceSectionViewModel(settings.Object, post: action => action()) { PreviewBubbleAutoHideSeconds = 3.75 };

        settings.Verify(
            s => s.Save(It.Is<AppSettings>(a => a.PreviewBubbleAutoHideMilliseconds == 3750)),
            Times.Once);
    }

    [Fact]
    public void SettingSecondsAboveMax_ClampsToFiveSecondsOnPersist()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        _ = new AppearanceSectionViewModel(settings.Object, post: action => action()) { PreviewBubbleAutoHideSeconds = 7.0 };

        settings.Verify(
            s => s.Save(It.Is<AppSettings>(a => a.PreviewBubbleAutoHideMilliseconds == 5000)),
            Times.Once);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(120.0, null, false)]
    [InlineData(null, 80.0, false)]
    [InlineData(120.0, 80.0, true)]
    public void IsOverlayPositionCustomized_RequiresBothFields(
        double? left,
        double? top,
        bool expected)
    {
        var settings = CreateSettingsMock(
            AppSettings.Default with
            {
                OverlayCustomLeft = left,
                OverlayCustomTop = top,
            });

        var sut = new AppearanceSectionViewModel(settings.Object, post: action => action());

        Assert.Equal(expected, sut.IsOverlayPositionCustomized);
    }

    [Fact]
    public void ResetOverlayPositionCommand_ClearsBothFields()
    {
        var settings = CreateSettingsMock(
            AppSettings.Default with
            {
                OverlayCustomLeft = 120.0,
                OverlayCustomTop = 80.0,
            });
        var sut = new AppearanceSectionViewModel(settings.Object, post: action => action());

        sut.ResetOverlayPositionCommand.Execute(null);

        settings.Verify(
            s => s.Save(
                It.Is<AppSettings>(a =>
                    a.OverlayCustomLeft == null && a.OverlayCustomTop == null)),
            Times.Once);
    }

    [Fact]
    public void Refresh_PropagatesIsOverlayPositionCustomized()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        var sut = new AppearanceSectionViewModel(settings.Object, post: action => action());
        Assert.False(sut.IsOverlayPositionCustomized);

        var propertyChanged = new List<string?>();
        sut.PropertyChanged += (_, e) => propertyChanged.Add(e.PropertyName);

        var updated = AppSettings.Default with
        {
            OverlayCustomLeft = 250.0,
            OverlayCustomTop = 150.0,
        };
        settings.SetupGet(s => s.Current).Returns(updated);
        settings.Raise(s => s.SettingsChanged += null, updated);

        Assert.True(sut.IsOverlayPositionCustomized);
        Assert.Contains(nameof(AppearanceSectionViewModel.IsOverlayPositionCustomized), propertyChanged);
    }

    [Fact]
    public void QueuedRefreshes_ApplyNewestSettings_WithoutPersistingStaleValues()
    {
        var settings = CreateSettingsMock(AppSettings.Default);
        var queued = new List<Action>();
        var sut = new AppearanceSectionViewModel(settings.Object, post: queued.Add);

        // Two commits land before the dispatcher drains, as happens when a background save
        // (dictation, model-storage migration) races the UI.
        var first = AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 2000 };
        settings.SetupGet(s => s.Current).Returns(first);
        settings.Raise(s => s.SettingsChanged += null, first);

        var second = AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 4000 };
        settings.SetupGet(s => s.Current).Returns(second);
        settings.Raise(s => s.SettingsChanged += null, second);

        settings.Invocations.Clear();
        foreach (var action in queued)
        {
            action();
        }

        // Both refreshes read Current, so the superseded 2000 is never applied, and hydrating
        // the view model must not write anything back.
        Assert.Equal(4.0, sut.PreviewBubbleAutoHideSeconds);
        settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
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
