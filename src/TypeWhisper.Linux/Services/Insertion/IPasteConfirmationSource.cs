namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
///     Optional source of positive "the paste actually landed" signals. After sending
///     Ctrl+V the insertion path must keep our text on the clipboard until the target
///     app has read it — restoring the user's previous clipboard too early cuts off the
///     in-flight transfer and the app pastes nothing (or the old content). A confirmation
///     source lets that restore be event-driven instead of a fixed delay.
///     <para>
///         Two-phase on purpose: the paste's confirmation event fires while the Ctrl+V
///         keystroke is being processed — before the restore step ever runs — so a
///         subscribe-then-wait inside the restore misses it every time and burns the full
///         timeout. The caller arms a watch via <see cref="BeginWatch" /> BEFORE sending
///         the keystroke and awaits <see cref="IPasteWatch.WaitAsync" /> after; an event
///         that arrives in between is latched by the watch, not lost.
///     </para>
/// </summary>
public interface IPasteConfirmationSource
{
    /// <summary>
    ///     Read-only diagnostic: whether the underlying source currently knows a focused
    ///     element, or <c>null</c> when the source is not running. Logged (env-gated) at
    ///     Ctrl+V time to judge whether a pre-paste focus gate would ever be needed.
    /// </summary>
    bool? HasFocusedElement { get; }

    /// <summary>
    ///     Starts watching for an insertion signal; call BEFORE sending the paste
    ///     keystroke. Returns <c>null</c> when the source is not running (feature off) —
    ///     indeterminate, the caller falls back to its fixed floor delay exactly as if no
    ///     confirmer were wired.
    /// </summary>
    IPasteWatch? BeginWatch();
}

/// <summary>
///     A live confirmation watch armed by <see cref="IPasteConfirmationSource.BeginWatch" />.
///     Dispose to stop watching — the owner must dispose it on every path so the
///     underlying event subscription never outlives the insertion.
/// </summary>
public interface IPasteWatch : IDisposable
{
    /// <summary>
    ///     Waits up to <paramref name="timeout" /> for a positive insertion signal, and
    ///     completes immediately when one was already latched between
    ///     <see cref="IPasteConfirmationSource.BeginWatch" /> and this call.
    ///     <c>true</c> = insertion positively observed; <c>null</c> = indeterminate
    ///     (no event within the window). Never returns <c>false</c>: absence of an
    ///     a11y event is not proof of absence.
    /// </summary>
    Task<bool?> WaitAsync(TimeSpan timeout, CancellationToken ct);
}
