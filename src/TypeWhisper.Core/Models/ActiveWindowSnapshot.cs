namespace TypeWhisper.Core.Models;

/// <summary>
/// Cross-platform snapshot of the currently focused window. Filled by an
/// <see cref="Interfaces.IActiveWindowProvider"/> chain (xdotool / Hyprland /
/// Sway / KWin / GNOME Introspect on Linux, UIA on Windows, AX on macOS).
///
/// Any field may be null when the underlying provider cannot supply it —
/// callers must treat the whole snapshot as "best effort". The
/// <see cref="Source"/> string is the provider name (e.g. "xdotool",
/// "hyprland", "kwin") so downstream UI / logs can attribute matches.
/// </summary>
public sealed record ActiveWindowSnapshot(
    string? ProcessName,
    string? Title,
    string? WindowId,
    string? AppId,
    string Source,
    bool IsTrusted);
