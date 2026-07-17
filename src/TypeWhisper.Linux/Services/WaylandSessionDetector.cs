namespace TypeWhisper.Linux.Services;

/// <summary>
///     Single source of truth for "is this session Wayland?" (setup,
///     backend selection, and the Shortcuts UI used to disagree by reading different
///     env vars). The runtime signal is a nonempty <c>WAYLAND_DISPLAY</c> — the actual
///     Wayland display connection the process would use — not <c>XDG_SESSION_TYPE</c>,
///     which some manually launched or minimal compositors never set.
/// </summary>
public static class WaylandSessionDetector
{
    public static bool IsWaylandSession()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
    }
}
