using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
///     Confirms a clipboard paste landed by watching the already-running AT-SPI event
///     stream: the first <c>object:text-changed</c> event after Ctrl+V means the target
///     inserted something, so the clipboard restore can proceed immediately instead of
///     sitting out a fixed worst-case delay. The watch must be armed BEFORE the keystroke
///     (<see cref="BeginWatch" />) — the paste's event fires while Ctrl+V is being
///     processed, so a subscription made in the restore step arrives too late and misses
///     it every time.
///     <para>
///         This class never starts the AT-SPI listeners itself — <see cref="IAtSpiEventClient.EnsureStartedAsync" />
///         is a privacy/consent decision owned by the correction-learning feature, and
///         registering listeners is precisely what perturbs GTK's main loop. When the
///         client is not running <see cref="BeginWatch" /> returns <c>null</c>
///         (indeterminate) immediately, so the feature-off insertion path is unchanged;
///         the confirmer only engages when the feature is already on — the only case the
///         restore race widens AND the only case the events are flowing.
///     </para>
/// </summary>
public sealed class AtSpiPasteConfirmation : IPasteConfirmationSource
{
    private readonly IAtSpiEventClient _client;

    public AtSpiPasteConfirmation(IAtSpiEventClient client)
    {
        _client = client;
    }

    public bool? HasFocusedElement =>
        _client.IsRunning ? _client.CurrentFocusedElement is not null : null;

    public IPasteWatch? BeginWatch()
    {
        return _client.IsRunning ? new AtSpiPasteWatch(_client) : null;
    }

    private sealed class AtSpiPasteWatch : IPasteWatch
    {
        private readonly IAtSpiEventClient _client;

        // The application (unique bus name) holding focus when the watch was armed — i.e.
        // where the paste is about to land. Null when no focus is known; then any app's
        // event has to count.
        private readonly string? _targetBusName;

        private readonly TaskCompletionSource<bool> _textChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // Keeps AT-SPI object:text-changed registered with the registry for the life of the watch.
        // The correction-learning feature registers it only while a field is armed, so without our
        // own lease a paste with no armed field would observe no text-changed at all.
        private readonly IDisposable _textEventsLease;

        internal AtSpiPasteWatch(IAtSpiEventClient client)
        {
            _client = client;
            _targetBusName = client.CurrentFocusedElement?.BusName;
            // Acquire the text-changed lease and subscribe here — before the caller sends Ctrl+V —
            // so the paste's text-changed can never fire unobserved; one that arrives before
            // WaitAsync latches in the TCS and the later await completes instantly. The registry
            // RegisterEvent this triggers is fire-and-forget: its propagation is fast relative to
            // the clipboard staging that follows the keystroke, and if the very first event still
            // races ahead of it, the watch simply degrades to the existing timeout fallback.
            _textEventsLease = client.AcquireTextChangedEvents();
            _client.TextChanged += OnTextChanged;
        }

        public async Task<bool?> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            // Already latched between BeginWatch and this call — confirm without
            // spinning up the timeout timer at all.
            if (_textChanged.Task.IsCompleted)
            {
                return true;
            }

            var completed = await Task.WhenAny(_textChanged.Task, Task.Delay(timeout, ct))
                .ConfigureAwait(false);
            if (completed == _textChanged.Task)
            {
                return true;
            }

            // Propagates OperationCanceledException when ct fired; otherwise the window
            // elapsed without an event — indeterminate, never false (some targets simply
            // don't emit text-changed).
            await completed.ConfigureAwait(false);
            return null;
        }

        public void Dispose()
        {
            _client.TextChanged -= OnTextChanged;
            // Release the text-changed lease alongside the unsubscribe: once no watch (and no
            // armed field) holds it, the registration is dropped so idle GTK apps stop emitting.
            _textEventsLease.Dispose();
        }

        // First TextChanged from the TARGET APPLICATION counts — matched by unique bus
        // name, never by element: the text-changed source object routinely differs from
        // the focus object (containers, sibling widgets), but it always belongs to the
        // same app connection. Without the app match, a background app's text event (an
        // arriving chat message, a ticking log view) would falsely confirm the paste and
        // restore the clipboard before the real target consumed it. When no focused app
        // was known at arm time, fall back to any-app (indeterminate targets).
        private void OnTextChanged(AtSpiElementRef element)
        {
            if (
                _targetBusName is null
                || string.Equals(element.BusName, _targetBusName, StringComparison.Ordinal)
            )
            {
                _textChanged.TrySetResult(true);
            }
        }
    }
}
