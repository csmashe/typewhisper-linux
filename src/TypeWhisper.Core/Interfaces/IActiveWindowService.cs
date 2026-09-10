using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Inspects the currently focused window so dictation can match per-app profiles
///     and capture the active browser URL. The getters return <c>null</c> when the
///     value can't be determined.
/// </summary>
public interface IActiveWindowService
{
    /// <summary>Process name of the focused window, or <c>null</c> if it can't be determined.</summary>
    string? GetActiveWindowProcessName();

    /// <summary>Title of the focused window, or <c>null</c> if it can't be determined.</summary>
    string? GetActiveWindowTitle();

    /// <summary>
    ///     Best-effort URL of the focused browser tab. When <paramref name="allowInteractiveCapture" />
    ///     is <c>true</c>, falls back to focusing the address bar (xdotool/xclip) if the quieter
    ///     methods (AT-SPI, title parsing) come up empty; otherwise only the non-intrusive methods run.
    /// </summary>
    string? GetBrowserUrl(bool allowInteractiveCapture = true);

    /// <summary>Snapshot of the focused window from the provider chain, or <c>null</c>.</summary>
    Task<ActiveWindowSnapshot?> GetActiveWindowSnapshotAsync(CancellationToken ct);

    /// <summary>
    ///     Best-effort non-interactive browser URL for <paramref name="snapshot" />.
    ///     Set <paramref name="honorMissBackoff" /> only for high-frequency UI polling;
    ///     dictation leaves it <c>false</c> so a poll miss never suppresses its own URL walk.
    /// </summary>
    string? GetBrowserUrlForSnapshot(ActiveWindowSnapshot? snapshot, bool honorMissBackoff = false);

    /// <summary>Returns distinct process names of all visible windows (sorted).</summary>
    IReadOnlyList<string> GetRunningAppProcessNames();
}
