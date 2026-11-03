using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class WaylandSessionDetectorTests
{
    [Theory]
    [InlineData("wayland-0", null, true, true)]
    [InlineData(null, "wayland", true, false)]
    [InlineData(null, null, false, false)]
    [InlineData("", null, false, false)]
    [InlineData("   ", null, false, false)]
    [InlineData(null, " WAYLAND ", true, false)]
    public void Predicates_ClassifyEnvironment(
        string? waylandDisplay,
        string? xdgSessionType,
        bool expectedSession,
        bool expectedDisplay
    )
    {
        Assert.Equal(
            expectedSession,
            WaylandSessionDetector.IsWaylandSession(waylandDisplay, xdgSessionType)
        );
        Assert.Equal(
            expectedDisplay,
            WaylandSessionDetector.HasWaylandDisplay(waylandDisplay)
        );
    }

    [Fact]
    public void IsWaylandSession_XdgWaylandWithoutWaylandDisplay_ReturnsTrue()
    {
        Assert.True(WaylandSessionDetector.IsWaylandSession(null, "wayland"));
    }

    [Fact]
    public void HasWaylandDisplay_XdgWaylandWithoutWaylandDisplay_ReturnsFalse()
    {
        Assert.False(WaylandSessionDetector.HasWaylandDisplay(null));
    }
}
