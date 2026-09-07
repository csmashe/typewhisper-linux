using Avalonia;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Pure placement/state coverage for the mapped-once dictation overlay. These tests require no
///     Avalonia window, X server, Wayland compositor, input device, or per-user filesystem state.
/// </summary>
public sealed class DictationOverlayWindowTests
{
    private static readonly PixelRect s_primary = new(0, 0, 1920, 1080);
    private static readonly PixelSize s_overlaySize = new(320, 56);

    [Fact]
    public void Hiding_ParksBeyondSuppliedMonitorBounds()
    {
        var state = new DictationOverlayPlacementState();
        state.Show();

        var parked = state.Hide(
            [s_primary],
            new PixelPoint(800, 1012)
        );

        Assert.False(state.IsShown);
        Assert.Equal(s_primary.Right + 1, parked.X);
        Assert.True(parked.X > s_primary.Right);
    }

    [Fact]
    public void RepositionWhileHidden_KeepsWindowParked()
    {
        var state = new DictationOverlayPlacementState();
        var parked = state.Hide(
            [s_primary],
            new PixelPoint(800, 1012)
        );

        var afterReposition = state.Reposition(
            new PixelPoint(800, 12),
            [s_primary],
            parked
        );

        Assert.Equal(parked, afterReposition);
        Assert.True(afterReposition.X > s_primary.Right);
    }

    [Theory]
    [InlineData(OverlayPosition.Top, 740, -188)]
    [InlineData(OverlayPosition.Bottom, 740, 632)]
    public void Showing_RestoresConfiguredPosition(
        OverlayPosition overlayPosition,
        int expectedX,
        int expectedY
    )
    {
        var workArea = new PixelRect(100, -200, 1600, 900);
        var state = new DictationOverlayPlacementState();
        var parked = state.Hide(
            [workArea],
            new PixelPoint(expectedX, expectedY)
        );
        Assert.True(parked.X > workArea.Right);

        state.Show();
        var configured = DictationOverlayPlacementState.ComputeConfiguredPosition(
            overlayPosition,
            workArea,
            s_overlaySize
        );
        var restored = state.Reposition(
            configured,
            [workArea],
            parked
        );

        Assert.True(state.IsShown);
        Assert.Equal(new PixelPoint(expectedX, expectedY), restored);
    }

    [Fact]
    public void Parking_WithNegativeOriginMonitorLayout_StaysBeyondEveryScreen()
    {
        PixelRect[] screens =
        [
            new(-1920, 0, 1920, 1080),
            new(0, -1200, 1920, 1200),
            new(0, 0, 2560, 1440),
        ];
        var state = new DictationOverlayPlacementState();

        var parked = state.Hide(
            screens,
            new PixelPoint(-960, 1012)
        );

        Assert.Equal(2561, parked.X);
        Assert.All(screens, screen => Assert.True(parked.X > screen.Right));
    }
}
