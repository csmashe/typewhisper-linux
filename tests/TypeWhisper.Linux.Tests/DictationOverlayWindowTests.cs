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

    [Fact]
    public void DragSession_CaptureLostWhileStartingNativeMove_KeepsDragActive()
    {
        // X11's BeginMoveDrag drops our pointer capture synchronously while handing the pointer
        // to the WM; the window mirrors this by ignoring capture loss while IsStartingMove.
        var session = new DictationOverlayDragSession();
        var captureLostDuringStart = false;

        session.Begin(() => captureLostDuringStart = session.IsStartingMove);

        Assert.True(captureLostDuringStart);
        Assert.False(session.IsStartingMove);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void DragSession_WmMovesAfterNativeMoveStarts_CompleteReturnsDropPosition()
    {
        var session = new DictationOverlayDragSession();
        session.Begin(() => { });

        session.RecordMove(new PixelPoint(3000, 400));
        session.RecordMove(new PixelPoint(3298, 699));

        Assert.Equal(new PixelPoint(3298, 699), session.Complete());
        Assert.False(session.IsActive);
    }

    [Fact]
    public void DragSession_PressWithoutMove_CompleteReturnsNothing()
    {
        var session = new DictationOverlayDragSession();
        session.Begin(() => { });

        Assert.Null(session.Complete());
    }

    [Fact]
    public void DragSession_MovesOutsideADrag_AreNotRecorded()
    {
        var session = new DictationOverlayDragSession();
        session.RecordMove(new PixelPoint(10, 10));
        session.Begin(() => { });
        session.Complete();

        session.RecordMove(new PixelPoint(20, 20));
        session.Begin(() => { });

        Assert.Null(session.Complete());
    }

    [Fact]
    public void DragSession_PauseMidMove_SavesPositionAndKeepsTracking()
    {
        var session = new DictationOverlayDragSession();
        session.Begin(() => { });
        session.RecordMove(new PixelPoint(3000, 400));

        Assert.Equal(new PixelPoint(3000, 400), session.Settle());
        Assert.True(session.IsActive);
        Assert.False(session.IsMoving);

        // The WM's move resumes after the pause; its final drop must still be captured.
        session.RecordMove(new PixelPoint(3298, 699));

        Assert.True(session.IsMoving);
        Assert.Equal(new PixelPoint(3298, 699), session.Complete());
        Assert.False(session.IsActive);
    }

    [Fact]
    public void DragSession_CompleteAfterSettleWithoutFurtherMoves_ReturnsNothing()
    {
        var session = new DictationOverlayDragSession();
        session.Begin(() => { });
        session.RecordMove(new PixelPoint(3000, 400));
        session.Settle();

        Assert.Null(session.Complete());
    }
}
