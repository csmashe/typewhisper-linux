namespace TypeWhisper.Linux.Services;

/// <summary>
///     Single source of truth for Wayland environment detection. Logical-session
///     consumers (hotkey scope, backend selection, and session-type reporting) use
///     <see cref="IsWaylandSession()" />. Socket-dependent tool selection
///     (<c>wl-copy</c>/<c>wl-paste</c>) uses <see cref="HasWaylandDisplay()" />.
/// </summary>
public static class WaylandSessionDetector
{
    public static bool IsWaylandSession()
    {
        return IsWaylandSession(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")
        );
    }

    internal static bool IsWaylandSession(string? waylandDisplay, string? xdgSessionType)
    {
        return HasWaylandDisplay(waylandDisplay)
               || string.Equals(
                   xdgSessionType?.Trim(),
                   "wayland",
                   StringComparison.OrdinalIgnoreCase
               );
    }

    public static bool HasWaylandDisplay()
    {
        return HasWaylandDisplay(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    internal static bool HasWaylandDisplay(string? waylandDisplay)
    {
        return !string.IsNullOrWhiteSpace(waylandDisplay);
    }
}
