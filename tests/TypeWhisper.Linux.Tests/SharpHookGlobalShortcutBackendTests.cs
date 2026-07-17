using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SharpHookGlobalShortcutBackendTests
{
    [Fact]
    public async Task IsGlobalScope_WaylandDisplaySetWithoutSessionType_ReturnsFalse()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            await using var backend = new SharpHookGlobalShortcutBackend();

            Assert.False(backend.IsGlobalScope);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }

    [Fact]
    public async Task IsGlobalScope_NoWaylandDisplayOrSessionType_ReturnsTrue()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            await using var backend = new SharpHookGlobalShortcutBackend();

            Assert.True(backend.IsGlobalScope);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }
}
