using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class WaylandSessionDetectorTests
{
    [Fact]
    public void IsWaylandSession_WaylandDisplaySetWithoutSessionType_ReturnsTrue()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);

            Assert.True(WaylandSessionDetector.IsWaylandSession());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }

    [Fact]
    public void IsWaylandSession_NoWaylandDisplayOrSessionType_ReturnsFalse()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);

            Assert.False(WaylandSessionDetector.IsWaylandSession());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }

    [Fact]
    public void IsWaylandSession_SessionTypeWaylandWithoutWaylandDisplay_ReturnsFalse()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");

            Assert.False(WaylandSessionDetector.IsWaylandSession());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }

    [Fact]
    public void IsWaylandSession_EmptyWaylandDisplay_ReturnsFalse()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);

            Assert.False(WaylandSessionDetector.IsWaylandSession());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }
}
