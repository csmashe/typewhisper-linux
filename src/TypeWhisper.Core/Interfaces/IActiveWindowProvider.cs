using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     One slot in the active-window provider chain. Each implementation targets a
///     single compositor (xdotool, hyprctl, swaymsg, KWin, GNOME Shell Introspect).
///     Providers should be cheap and fail fast — gating on env vars before shelling
///     out avoids paying for irrelevant compositor probes.
/// </summary>
public interface IActiveWindowProvider
{
    /// <summary>
    ///     Stable identifier surfaced in <see cref="ActiveWindowSnapshot.Source" />
    ///     and used in failure-tracker remediation text.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     True if this provider's compositor appears to be active.
    ///     The orchestrator skips providers that return false (e.g. no Sway probe inside GNOME).
    /// </summary>
    bool IsApplicable();

    /// <summary>
    ///     Returns the focused window snapshot, or <c>null</c> if this provider cannot
    ///     determine one (helper missing, no focused client, transient failure).
    ///     <c>null</c> means "skip and try next" — do not throw for the normal unknown case.
    ///     Non-cancellation failures (missing binary, bad output) are swallowed and returned as <c>null</c>.
    /// </summary>
    Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct);
}