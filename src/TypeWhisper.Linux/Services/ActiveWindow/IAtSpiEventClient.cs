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
    ///     Idempotent — subsequent calls return the cached availability.
    /// </summary>
    Task<bool> EnsureStartedAsync();

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
    ///     Best-effort sweep over the applications on the a11y bus, touching each unseen
    ///     app's tree once (Accessible.GetAttributes/GetRelationSet). Chromium/Electron apps
    ///     expose only a stub tree until an assistive tool makes such a call — it is their
    ///     "someone is reading me" signal — so this unlocks their text for correction
    ///     learning. Harmless no-op for other toolkits; per-app failures are swallowed.
    ///     No-op when the client is not connected.
    /// </summary>
    Task PokeAccessibilityTreesAsync();
}
