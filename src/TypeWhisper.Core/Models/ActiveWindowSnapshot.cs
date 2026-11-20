namespace TypeWhisper.Core.Models;

/// <summary>
///     Cross-platform snapshot of the currently focused window. Filled by an
///     <see cref="Interfaces.IActiveWindowProvider" /> chain (xdotool / Hyprland /
///     Sway / KWin / GNOME Introspect on Linux, UIA on Windows, AX on macOS).
///     Any field may be null when the underlying provider cannot supply it —
///     callers must treat the whole snapshot as "best effort". The
///     <see cref="Source" /> string is the provider name (e.g. "xdotool",
///     "hyprland", "kwin") so downstream UI / logs can attribute matches.
///     <see cref="ProcessId" /> is the window owner's PID when the provider reports one.
///     <see cref="ProcessIdUnresolved" /> marks a provider that always reports a PID but
///     couldn't this time (timeout or failure): the window's owner is unknown, so callers
///     must not attribute it to another window by name and title.
/// </summary>
public sealed record ActiveWindowSnapshot(
    string? ProcessName,
    string? Title,
    string? WindowId,
    string? AppId,
    string Source,
    int? ProcessId = null,
    bool ProcessIdUnresolved = false
);