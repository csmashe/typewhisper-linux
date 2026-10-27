using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     On a Wayland session only XWayland clients report global SCREEN extents. The pairs below are
///     the SCREEN/WINDOW boxes real apps returned on a three-monitor GNOME Wayland desktop.
/// </summary>
public sealed class AtSpiGlobalExtentsTests
{
    [Theory]
    // XWayland apps: SCREEN = WINDOW + the top-level's on-screen origin.
    [InlineData(1942, 240, 22, 24)] // window on the middle monitor
    [InlineData(4430, 145, 20, 26)] // window on the right monitor
    [InlineData(20, 26 + 32, 20, 26)] // left monitor, window just under GNOME's top bar
    public void XWaylandClient_IsGlobal(int screenX, int screenY, int windowX, int windowY)
    {
        Assert.True(
            AtSpiEventClient.AreGlobalExtents(Rect(screenX, screenY), Rect(windowX, windowY))
        );
    }

    [Theory]
    // GTK3 (the QA repro), Chromium/Electron and Firefox: SCREEN == WINDOW.
    [InlineData(38, 122, 38, 122)]
    [InlineData(158, 51, 158, 51)]
    // GTK4: SCREEN shifted by a meaningless negative offset.
    [InlineData(0, 0, 0, 120)]
    [InlineData(0, 0, -1, 5)]
    public void NativeWaylandClient_IsNotGlobal(int screenX, int screenY, int windowX, int windowY)
    {
        Assert.False(
            AtSpiEventClient.AreGlobalExtents(Rect(screenX, screenY), Rect(windowX, windowY))
        );
    }

    private static AtSpiScreenRect Rect(int x, int y)
    {
        return new AtSpiScreenRect(x, y, 626, 226);
    }
}
