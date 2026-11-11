using Avalonia;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Placement geometry for the learned-corrections toast: below the element by default,
///     flipped above at the screen's bottom edge, clamped inside the containing screen, and a
///     bottom-center fallback when the element's extents are missing or implausible. Pure math,
///     so no Avalonia window/lifetime is needed.
/// </summary>
public sealed class LearnedToastPlacementTests
{
    // A single 1920×1080 primary screen at the origin, used by most cases.
    private static readonly PixelRect s_primary = new(0, 0, 1920, 1080);

    // A typical toast footprint.
    private static readonly PixelSize s_toast = new(300, 70);

    [Fact]
    public void PlausibleExtents_PlacesBelowAndHorizontallyCentered()
    {
        // Element near the middle of the screen: the toast sits just below it, centered.
        var element = new AtSpiScreenRect(X: 800, Y: 400, Width: 200, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        // Centered: elementX + (elementW - toastW)/2 = 800 + (200-300)/2 = 750.
        Assert.Equal(750, point.X);
        // Below: elementBottom + gap(12) = 440 + 12 = 452.
        Assert.Equal(452, point.Y);
    }

    [Fact]
    public void ElementAtBottomEdge_FlipsToastAbove()
    {
        // Element flush against the bottom: below would clip, so the toast flips above it.
        var element = new AtSpiScreenRect(X: 800, Y: 1040, Width: 200, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        // Above: elementY - gap(12) - toastH(70) = 1040 - 12 - 70 = 958.
        Assert.Equal(958, point.Y);
        Assert.True(point.Y + s_toast.Height <= s_primary.Bottom, "toast must stay on-screen");
    }

    [Fact]
    public void ElementNearLeftEdge_ClampsToastFullyOnScreen()
    {
        // Element hugging the left edge: centering would push the toast off the left, so X clamps
        // to the screen's left edge (0).
        var element = new AtSpiScreenRect(X: 0, Y: 400, Width: 40, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        Assert.Equal(0, point.X);
    }

    [Fact]
    public void ElementNearRightEdge_ClampsToastFullyOnScreen()
    {
        // Element hugging the right edge: the toast clamps so its right edge sits on the screen's.
        var element = new AtSpiScreenRect(X: 1900, Y: 400, Width: 20, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        Assert.Equal(s_primary.Right - s_toast.Width, point.X); // 1920 - 300 = 1620
        Assert.True(point.X + s_toast.Width <= s_primary.Right);
    }

    [Fact]
    public void MissingExtents_FallsBackToBottomCenterOfPrimary()
    {
        var point = LearnedToastPlacement.Compute(source: null, [s_primary], s_toast);

        // Horizontally centered on primary: (1920-300)/2 = 810.
        Assert.Equal(810, point.X);
        // 80px inset from the bottom: 1080 - 70 - 80 = 930.
        Assert.Equal(930, point.Y);
    }

    [Fact]
    public void ZeroSizeExtents_TreatedAsImplausible_FallsBack()
    {
        // Native-Wayland apps often report a 0,0/0×0 box; that must fall back, not place at 0,0.
        var element = new AtSpiScreenRect(X: 0, Y: 0, Width: 0, Height: 0);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        Assert.Equal(810, point.X);
        Assert.Equal(930, point.Y);
    }

    [Fact]
    public void OffScreenExtents_TreatedAsImplausible_FallsBack()
    {
        // Positive size but entirely off every screen (window-relative junk shifted way out):
        // implausible, so fall back rather than place the toast where nothing is visible.
        var element = new AtSpiScreenRect(X: 50_000, Y: 50_000, Width: 200, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary], s_toast);

        Assert.Equal(810, point.X);
        Assert.Equal(930, point.Y);
    }

    [Fact]
    public void ExtentsOnSecondaryMonitor_PlacesOnThatMonitor()
    {
        // Two side-by-side 1920-wide screens; the element lives on the right-hand secondary one.
        var secondary = new PixelRect(1920, 0, 1920, 1080);
        var element = new AtSpiScreenRect(X: 2800, Y: 400, Width: 200, Height: 40);

        var point = LearnedToastPlacement.Compute(element, [s_primary, secondary], s_toast);

        // Centered on the element (2800 + (200-300)/2 = 2750), which lands on the secondary
        // screen — not clamped back onto the primary.
        Assert.Equal(2750, point.X);
        Assert.True(point.X >= secondary.X, "toast must sit on the secondary monitor");
        Assert.Equal(452, point.Y); // below the element: 440 + 12
    }

    [Fact]
    public void WindowFrame_AtUnitScale_MapsUnchanged()
    {
        var window = new CompositorWindowFrame(new AtSpiScreenRect(2000, 150, 720, 360), new AtSpiScreenRect(0, 0, 3840, 1080));
        PixelRect[] screens = [s_primary, new(1920, 0, 1920, 1080)];

        Assert.Equal(new AtSpiScreenRect(2000, 150, 720, 360), LearnedToastPlacement.MapToScreens(window, screens));
    }

    [Fact]
    public void WindowFrame_UnderScaledXwayland_MapsOntoTheSameMonitor()
    {
        // Mutter logical: 1280x800 at 100% + 1280x720 (1600x900 at 125%) to its right. Xwayland
        // reports the whole desktop at 2x, so the secondary's X screen starts at 2560.
        var window = new CompositorWindowFrame(new AtSpiScreenRect(1500, 150, 720, 360), new AtSpiScreenRect(0, 0, 2560, 800));
        PixelRect[] screens = [new(0, 0, 2560, 1600), new(2560, 0, 2560, 1440)];

        var anchor = LearnedToastPlacement.MapToScreens(window, screens);
        var point = LearnedToastPlacement.Compute(anchor, screens, new PixelSize(758, 140));

        Assert.Equal(new AtSpiScreenRect(3000, 300, 1440, 720), anchor);
        Assert.Equal(new PixelPoint(3341, 1032), point);
    }

    [Fact]
    public void WindowFrame_WithoutScreens_IsNotMapped()
    {
        var window = new CompositorWindowFrame(new AtSpiScreenRect(10, 10, 100, 100), new AtSpiScreenRect(0, 0, 1920, 1080));

        Assert.Null(LearnedToastPlacement.MapToScreens(window, []));
    }

    [Fact]
    public void Anchor_PrefersPlausibleExtentsOverTheWindowFrame()
    {
        var extents = new AtSpiScreenRect(2100, 300, 300, 40);
        var window = new CompositorWindowFrame(new AtSpiScreenRect(2000, 150, 720, 360), new AtSpiScreenRect(0, 0, 3840, 1080));

        Assert.Equal(extents, LearnedToastPlacement.ChooseAnchor(extents, window, [s_primary, new PixelRect(1920, 0, 1920, 1080)]));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(5000, 5000, 300, 40)]
    public void Anchor_FallsBackToTheWindowFrameWhenExtentsAreUnusable(int x, int y, int width, int height)
    {
        var window = new CompositorWindowFrame(new AtSpiScreenRect(2000, 150, 720, 360), new AtSpiScreenRect(0, 0, 3840, 1080));

        Assert.Equal(
            new AtSpiScreenRect(2000, 150, 720, 360),
            LearnedToastPlacement.ChooseAnchor(new AtSpiScreenRect(x, y, width, height), window, [s_primary, new PixelRect(1920, 0, 1920, 1080)])
        );
    }

    [Fact]
    public void Anchor_RejectsExtentsOutsideTheWindowFrame()
    {
        // GTK with GDK_SCALE=2 on X11 reports half-size extents: a field on the right monitor
        // claims to be on the left one. The window's real frame keeps the toast on the right.
        PixelRect[] screens = [new(0, 0, 640, 1024), new(640, 0, 640, 1024)];
        var window = new CompositorWindowFrame(new AtSpiScreenRect(701, 474, 440, 300), null);

        var anchor = LearnedToastPlacement.ChooseAnchor(new AtSpiScreenRect(350, 237, 220, 34), window, screens);
        var point = LearnedToastPlacement.Compute(anchor, screens, new PixelSize(324, 70));

        Assert.Equal(new AtSpiScreenRect(701, 474, 440, 300), anchor);
        Assert.Equal(new PixelPoint(759, 786), point);
    }

    [Fact]
    public void Anchor_KeepsExtentsInsideAnX11FrameWhenTheRootIsWiderThanTheMonitors()
    {
        // An X11 frame is already in root coordinates. Stretching a 1920-wide root over these
        // 1280 pixels of monitors would shrink the frame to x 467–760 and reject the field.
        PixelRect[] screens = [new(0, 0, 640, 1024), new(640, 0, 640, 1024)];
        var window = new CompositorWindowFrame(new AtSpiScreenRect(701, 474, 440, 300), null);
        var extents = new AtSpiScreenRect(721, 500, 400, 34);

        Assert.Equal(extents, LearnedToastPlacement.ChooseAnchor(extents, window, screens));
    }
}
