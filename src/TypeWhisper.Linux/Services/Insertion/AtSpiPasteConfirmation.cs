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

        private readonly TaskCompletionSource<bool> _textChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal AtSpiPasteWatch(IAtSpiEventClient client)
        {
            _client = client;
            // Subscribed here — before the caller sends Ctrl+V — so the paste's
            // text-changed can never fire unobserved; one that arrives before
            // WaitAsync latches in the TCS and the later await completes instantly.
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
        }

        // First TextChanged from ANY element counts. Do not match against
        // CurrentFocusedElement and do not read the text back: focus events sometimes
        // yield containers without the Text interface (the `No such interface
        // "org.a11y.atspi.Text"` failure), and the text-changed source object routinely
        // differs from the focus object.
        private void OnTextChanged(AtSpiElementRef _)
        {
            _textChanged.TrySetResult(true);
        }
    }
}
