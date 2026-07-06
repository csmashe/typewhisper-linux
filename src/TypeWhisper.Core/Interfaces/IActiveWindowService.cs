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

    /// <summary>
    ///     Best-effort bounded snippet of the focused element's text (+ nearby labels), used as
    ///     read-only spelling reference for LLM cleanup. Opt-in and gated by the caller. Scoped to
    ///     the given window (<paramref name="processName" />/<paramref name="title" />, from the
    ///     recording's snapshot) so a focus change can't harvest a different app's screen. Returns
    ///     <c>null</c> when no a11y tree is available, on a password field, or when nothing
    ///     readable is in focus for that window.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- part of the window-inspection contract; consumed via the concrete ActiveWindowService today, kept on the interface for parity with the sibling getters
    string? GetFocusedScreenContext(string? processName, string? title);

    /// <summary>Returns distinct process names of all visible windows (sorted).</summary>
    IReadOnlyList<string> GetRunningAppProcessNames();
}
