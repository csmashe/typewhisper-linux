namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Identifies an accessible element on the AT-SPI bus as the pair
///     (unique D-Bus bus name of the owning application, object path). Value
///     equality lets callers compare "is this the same element I armed?".
/// </summary>
public readonly record struct AtSpiElementRef(string BusName, string ObjectPath)
{
    public bool IsValid => !string.IsNullOrEmpty(BusName) && !string.IsNullOrEmpty(ObjectPath);
}

/// <summary>
///     Screen-coordinate bounding box of an accessible element, as reported by
///     org.a11y.atspi.Component.GetExtents with ATSPI_COORD_TYPE_SCREEN: <see cref="X" />/
///     <see cref="Y" /> are the top-left corner in global screen pixels, <see cref="Width" />/
///     <see cref="Height" /> its size. Used to place the learned-corrections toast beside the
///     element the correction came from.
/// </summary>
public readonly record struct AtSpiScreenRect(int X, int Y, int Width, int Height);

/// <summary>
///     Event-driven view of the AT-SPI accessibility bus: a persistent connection
///     that surfaces focus/text-edit signals and one-shot text reads, without any
///     polling or per-read subprocess. Behind an interface so the correction-learning
///     orchestration can be unit-tested with a fake.
/// </summary>
public interface IAtSpiEventClient
{
    /// <summary>Raised when an element gains keyboard focus (object:state-changed:focused, gained).</summary>
    event Action<AtSpiElementRef>? FocusChanged;

    /// <summary>Raised when an element's text changes (object:text-changed, insert/delete).</summary>
    event Action<AtSpiElementRef>? TextChanged;

    /// <summary>The element that most recently gained focus, or <c>null</c> if none seen yet.</summary>
    AtSpiElementRef? CurrentFocusedElement { get; }

    /// <summary>
    ///     Snapshot of distinct recently focused elements, most recent first (the head is
    ///     <see cref="CurrentFocusedElement" />), bounded to a handful of entries. Some apps
    ///     (LibreOffice Writer) flap the focused state between the caret's text widget and a
    ///     structural pane that exposes no text, so the most recent element is not always the
    ///     readable one — consumers can fall back through this history.
    /// </summary>
    IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements();

    /// <summary>
    ///     <c>true</c> while the client holds a live a11y-bus connection with listeners
    ///     registered (a successful <see cref="EnsureStartedAsync" /> not yet undone by
    ///     <see cref="StopAsync" />). Read-only — never connects; consumers that must not
    ///     start the listeners themselves (privacy/consent lives with the feature toggle)
    ///     check this instead of calling <see cref="EnsureStartedAsync" />.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Connects to the a11y bus and registers event listeners on first call.
    ///     Returns <c>true</c> when the bus is reachable and listeners are live,
    ///     <c>false</c> when AT-SPI is unavailable (headless/minimal/remote sessions).
    ///     Idempotent — only a successful start is cached; a failed attempt leaves the
    ///     client able to retry, so a later call reconnects once the bus becomes available.
    /// </summary>
    Task<bool> EnsureStartedAsync();

    /// <summary>
    ///     Acquires a reference-counted lease on <c>object:text-changed</c> registration:
    ///     while at least one lease is held, the registry is told an AT wants text-changed
    ///     events, which is what makes on-demand toolkits (GTK) actually emit them. The first
    ///     lease registers, the last one disposed deregisters, so a session with nothing
    ///     tracking imposes no text-event traffic on every GTK app (a registered listener
    ///     makes terminals emit an event per output line — an accessibility-bus flood).
    ///     Dispose the returned handle when text events are no longer needed; disposing twice
    ///     is a no-op. Safe to call before <see cref="EnsureStartedAsync" /> — the lease is
    ///     honored the moment a connection is (re)established.
    /// </summary>
    IDisposable AcquireTextChangedEvents();

    /// <summary>
    ///     Tears down the event subscriptions and the a11y-bus connection and resets state
    ///     so a later <see cref="EnsureStartedAsync" /> reconnects fresh. Called when the
    ///     user disables the feature so the process stops receiving a11y event traffic.
    ///     Safe to call when never started; safe against a concurrent
    ///     <see cref="EnsureStartedAsync" /> (both serialize on the same start gate).
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     One-shot read of an element's text via org.a11y.atspi.Text, clamped to
    ///     <paramref name="maxLength" /> characters. Returns <c>null</c> when the
    ///     element does not expose readable text (e.g. Electron, terminals) or the
    ///     read fails.
    /// </summary>
    Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength);

    /// <summary>
    ///     <c>true</c> when the element's AT-SPI role is PASSWORD_TEXT, <c>false</c> when it is
    ///     positively a non-password role, and <c>null</c> when the role could not be read
    ///     (denied/transient/missing). Callers must treat <c>null</c> as unsafe and fail closed
    ///     — this guards a privacy boundary, so "unknown" must never be read as "safe".
    /// </summary>
    Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element);

    /// <summary>
    ///     Best-effort read of an element's on-screen bounding box via
    ///     org.a11y.atspi.Component.GetExtents (screen coordinates). Returns <c>null</c> when the
    ///     element doesn't implement the Component interface, the read fails, or the client isn't
    ///     connected. Used only to position feedback UI, so any failure is non-fatal — the caller
    ///     falls back to a fixed on-screen spot.
    /// </summary>
    Task<AtSpiScreenRect?> TryGetScreenExtentsAsync(AtSpiElementRef element);

    /// <summary>
    ///     Best-effort sweep over the applications on the a11y bus, touching each unseen
    ///     app's tree once (Accessible.GetAttributes/GetRelationSet). Chromium/Electron apps
    ///     expose only a stub tree until an assistive tool makes such a call — it is their
    ///     "someone is reading me" signal — so this unlocks their text for correction
    ///     learning. Harmless no-op for other toolkits; per-app failures are swallowed.
    ///     No-op when the client is not connected. The returned task completes when the
    ///     whole sweep has finished (every underlying call is time-bounded), so a caller
    ///     that needs the unlock NOW — the cold-start focus bootstrap — can await it.
    /// </summary>
    Task PokeAccessibilityTreesAsync();

    /// <summary>
    ///     Actively locates the element currently holding keyboard focus. The client only
    ///     ever observes focus changes that happen AFTER its listener registered — AT-SPI
    ///     replays nothing — so when the user focused the target field before the client
    ///     connected, <see cref="CurrentFocusedElement" /> stays <c>null</c> and waiting
    ///     cannot recover it. This scans the active window's subtree for the FOCUSED state,
    ///     primes <see cref="CurrentFocusedElement" /> and the recent-focus history with
    ///     what it finds, and returns the element. Returns the already-known element
    ///     immediately when a focus event has been seen; <c>null</c> when nothing holds
    ///     focus, the scan budget ran out, or the client is not connected. Bounded and
    ///     best-effort; concurrent callers share one in-flight scan.
    /// </summary>
    Task<AtSpiElementRef?> TryBootstrapFocusAsync();
}
