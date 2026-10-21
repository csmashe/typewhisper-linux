using Avalonia;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Floating live text: placement math, window content, the inline/floating switch in the overlay
///     and the Appearance controls. No Avalonia window or display server is needed.
/// </summary>
public sealed class LiveTextWindowTests
{
    private static readonly PixelRect s_workArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Fit_KeepsAPlacementThatIsInsideTheWorkArea()
    {
        var bounds = LiveTextWindowPlacement.Fit(new PixelPoint(100, 200), new PixelSize(420, 220), s_workArea);

        Assert.Equal(new PixelRect(100, 200, 420, 220), bounds);
    }

    [Theory]
    [InlineData(-300, -40, 0, 0)]
    [InlineData(1800, 1000, 1500, 820)]
    public void Fit_ClampsAnOffScreenPositionIntoTheWorkArea(int x, int y, int expectedX, int expectedY)
    {
        var bounds = LiveTextWindowPlacement.Fit(new PixelPoint(x, y), new PixelSize(420, 220), s_workArea);

        Assert.Equal(new PixelPoint(expectedX, expectedY), bounds.Position);
        Assert.Equal(new PixelSize(420, 220), bounds.Size);
    }

    [Fact]
    public void Fit_ShrinksAWindowLargerThanTheWorkAreaOfASecondMonitor()
    {
        var secondary = new PixelRect(1920, -200, 1280, 700);

        var bounds = LiveTextWindowPlacement.Fit(new PixelPoint(2000, 0), new PixelSize(3000, 2000), secondary);

        Assert.Equal(secondary, bounds);
    }

    [Fact]
    public void IndexOfMostOverlapped_KeepsAWindowOnTheMonitorHoldingMostOfIt()
    {
        // Primary on the right, a shorter secondary on the left; the corner hangs past its left edge.
        (PixelRect, double)[] screens = [(new PixelRect(0, 0, 1920, 1080), 1), (new PixelRect(-1280, 200, 1280, 720), 1)];

        Assert.Equal(1, LiveTextWindowPlacement.IndexOfMostOverlapped(screens, new PixelPoint(-1300, 300), 420, 220));
        Assert.Equal(0, LiveTextWindowPlacement.IndexOfMostOverlapped(screens, new PixelPoint(-100, 300), 420, 220));
        Assert.Equal(-1, LiveTextWindowPlacement.IndexOfMostOverlapped(screens, new PixelPoint(5000, 5000), 420, 220));
    }

    [Fact]
    public void IndexOfMostOverlapped_SizesTheWindowWithEachScreensScaling()
    {
        // 2x primary right of a 1x secondary: 300 of 420 px (71%) land on the secondary, while at 2x
        // the window would put 540 of 840 px (64%) on the primary.
        (PixelRect, double)[] screens = [(new PixelRect(0, 0, 3840, 2160), 2), (new PixelRect(-1920, 0, 1920, 1080), 1)];

        Assert.Equal(1, LiveTextWindowPlacement.IndexOfMostOverlapped(screens, new PixelPoint(-300, 100), 420, 220));
    }

    [Theory]
    [InlineData(OverlayPosition.Bottom, 740)]
    [InlineData(OverlayPosition.Top, 80)]
    public void DefaultPosition_SitsBesideTheOverlayOnTheConfiguredEdge(OverlayPosition overlay, int expectedY)
    {
        var position = LiveTextWindowPlacement.DefaultPosition(s_workArea, new PixelSize(420, 220), overlay);

        Assert.Equal(new PixelPoint(750, expectedY), position);
    }

    [Fact]
    public void ToPixels_ScalesAndNormalizesSavedSize()
    {
        Assert.Equal(new PixelSize(840, 440), LiveTextWindowPlacement.ToPixels(420, 220, 2.0));
        Assert.Equal(new PixelSize(280, 140), LiveTextWindowPlacement.ToPixels(1, 1, double.NaN));
    }

    [Fact]
    public void ToSavedSize_RoundsAndClampsTheClientSize()
    {
        Assert.Equal((501, 140), LiveTextWindowPlacement.ToSavedSize(new Size(500.6, 90)));
    }

    [Fact]
    public void Window_ShowsLiveTextOnlyWhenFloating()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var (overlay, sut) = Create(settings, new LiveTextPreviewService());

        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = true, PartialText = "hello world" });

        Assert.False(sut.HasVisibleContent);
        Assert.True(overlay.ShowInlinePartialText);

        settings.Update(current => current with { LiveTextFloating = true });

        Assert.True(sut.HasVisibleContent);
        Assert.Equal("hello world", sut.Text);
        Assert.False(sut.IsPreview);
        Assert.False(overlay.ShowInlinePartialText);
    }

    [Fact]
    public void Window_HidesWhenTheOverlayHidesEvenIfTextRemains()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { LiveTextFloating = true });
        var (overlay, sut) = Create(settings, new LiveTextPreviewService());

        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = true, PartialText = "hello" });
        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = false, PartialText = "hello" });

        Assert.False(sut.HasVisibleContent);
    }

    [Fact]
    public void Preview_ShowsSampleText_AndDictationEndsIt()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { LiveTextFloating = true });
        var preview = new LiveTextPreviewService();
        var (overlay, sut) = Create(settings, preview);

        preview.Start();

        Assert.True(sut.IsPreview);
        Assert.True(sut.HasVisibleContent);
        Assert.False(string.IsNullOrEmpty(sut.Text));

        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = true });

        Assert.False(preview.IsActive);
        Assert.False(sut.HasVisibleContent);
    }

    [Fact]
    public void Preview_StartedDuringDictation_IsRefused()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { LiveTextFloating = true });
        var preview = new LiveTextPreviewService();
        var (overlay, sut) = Create(settings, preview);

        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = true, PartialText = "hello" });
        preview.Start();
        overlay.ApplyState(new DictationOverlayState { IsOverlayVisible = false });

        Assert.False(preview.IsActive);
        Assert.False(sut.HasVisibleContent);
    }

    [Fact]
    public void Appearance_PlacementSizeAndResetPersist()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = new AppearanceSectionViewModel(settings, post: action => action());

        Assert.False(sut.IsLiveTextFloating);
        Assert.False(sut.ResetLiveTextWindowCommand.CanExecute(null));

        sut.SelectedLiveTextPlacement = sut.LiveTextPlacements.Single(option => option.IsFloating);
        sut.LiveTextWindowWidth = 10;
        sut.LiveTextWindowHeight = 300;

        Assert.True(settings.Current.LiveTextFloating);
        Assert.True(sut.IsLiveTextFloating);
        Assert.Equal(AppSettings.MinLiveTextWindowWidth, settings.Current.LiveTextWindowWidth);
        Assert.Equal(AppSettings.MinLiveTextWindowWidth, sut.LiveTextWindowWidth);
        Assert.Equal(300, settings.Current.LiveTextWindowHeight);
        Assert.True(sut.ResetLiveTextWindowCommand.CanExecute(null));

        settings.Update(current => current with { LiveTextWindowLeft = 40, LiveTextWindowTop = 60 });
        sut.ResetLiveTextWindowCommand.Execute(null);

        Assert.Null(settings.Current.LiveTextWindowLeft);
        Assert.Null(settings.Current.LiveTextWindowTop);
        Assert.Equal(AppSettings.DefaultLiveTextWindowWidth, settings.Current.LiveTextWindowWidth);
        Assert.Equal(AppSettings.DefaultLiveTextWindowHeight, settings.Current.LiveTextWindowHeight);
        Assert.True(settings.Current.LiveTextFloating);
    }

    [Fact]
    public void Appearance_PreviewTogglesAndEndsWhenLeavingOrAttaching()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { LiveTextFloating = true });
        var preview = new LiveTextPreviewService();
        var sut = new AppearanceSectionViewModel(settings, post: action => action(), preview: preview);

        sut.ToggleLiveTextPreviewCommand.Execute(null);
        Assert.True(sut.IsLiveTextPreviewActive);

        sut.StopLiveTextPreview();
        Assert.False(preview.IsActive);

        sut.ToggleLiveTextPreviewCommand.Execute(null);
        sut.SelectedLiveTextPlacement = sut.LiveTextPlacements.Single(option => !option.IsFloating);

        Assert.False(preview.IsActive);
        Assert.False(settings.Current.LiveTextFloating);
    }

    private static (DictationOverlayViewModel Overlay, LiveTextWindowViewModel Window) Create(
        ISettingsService settings,
        LiveTextPreviewService preview)
    {
        var overlay = new DictationOverlayViewModel(settings, static action => action());
        return (overlay, new LiveTextWindowViewModel(overlay, settings, preview, static action => action()));
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            Save(mutate(Current));
            return Current;
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
