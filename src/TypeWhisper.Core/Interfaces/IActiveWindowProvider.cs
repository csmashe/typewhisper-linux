using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// One slot in the active-window provider chain. Each implementation
/// targets a single compositor / display server (xdotool for X11+XWayland,
/// hyprctl, swaymsg, KWin scripting, GNOME Shell Introspect). The
/// orchestrator runs them in order and returns the first non-null
/// snapshot.
///
/// Providers are expected to be cheap and to fail fast — gating on env
/// vars (<c>HYPRLAND_INSTANCE_SIGNATURE</c>, <c>SWAYSOCK</c>,
/// <c>XDG_CURRENT_DESKTOP</c>) before shelling out keeps the chain from
/// paying for irrelevant compositor probes.
/// </summary>
public interface IActiveWindowProvider
{
    /// <summary>
    /// Stable identifier surfaced in <see cref="ActiveWindowSnapshot.Source"/>
    /// and used in failure-tracker remediation text.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True if this provider's compositor / display server appears to be
    /// the active one. The orchestrator skips providers that say false so
    /// e.g. a Sway probe never runs inside a GNOME session.
    /// </summary>
    bool IsApplicable();

    Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct);
}
