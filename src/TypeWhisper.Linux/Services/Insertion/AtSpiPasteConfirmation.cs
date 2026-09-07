using System.Diagnostics;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
///     Confirms a clipboard paste landed by watching the already-running AT-SPI event
///     stream and verifying that the changed element contains the exact expected clipboard
///     text. A same-application <c>object:text-changed</c> event alone is indeterminate.
///     The watch must be armed BEFORE the keystroke (<see cref="BeginWatch" />) — the
///     paste's event fires while Ctrl+V is being processed, so a subscription made in the
///     restore step arrives too late and misses it every time.
///     <para>
///         This class never starts the AT-SPI listeners itself — <see cref="IAtSpiEventClient.EnsureStartedAsync" />
///         is a privacy/consent decision owned by the correction-learning feature, and
///         registering listeners is precisely what perturbs GTK's main loop. When the
///         client is not running <see cref="BeginWatch" /> returns <c>null</c>
///         (indeterminate) immediately, so the feature-off insertion path is unchanged;
///         the confirmer only engages when the feature is already on — the only case the
///         restore race widens AND the only case the events are flowing.
///     </para>
///     <para>
///         Targets without a readable AT-SPI Text interface (including terminals and some
///         Electron surfaces) cannot positively confirm this way and fall through to the
///         existing timeout/floor delay. The exact-substring check also cannot prove the
///         caret position or inserted range: an unrelated edit to an element that already
///         contained the expected text can still satisfy the heuristic. Closing that gap
///         requires AT-SPI signal payload details that this event abstraction does not expose.
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

    public IPasteWatch? BeginWatch(string expectedText)
    {
        return _client.IsRunning ? new AtSpiPasteWatch(_client, expectedText) : null;
    }

    private sealed class AtSpiPasteWatch : IPasteWatch
    {
        private readonly IAtSpiEventClient _client;
        private readonly string _expectedText;
        private readonly int _readLength;

        // The application (unique bus name) holding focus when the watch was armed — i.e.
        // where the paste is about to land. This is a cheap first-pass filter; the changed
        // element's text must still contain the expected paste. When focus is unknown, the
        // content check prevents an arbitrary app's event alone from confirming delivery.
        private readonly string? _targetBusName;

        private readonly TaskCompletionSource<bool> _textChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // Keeps AT-SPI object:text-changed registered with the registry for the life of the watch.
        // The correction-learning feature registers it only while a field is armed, so without our
        // own lease a paste with no armed field would observe no text-changed at all.
        private readonly IDisposable _textEventsLease;

        internal AtSpiPasteWatch(IAtSpiEventClient client, string expectedText)
        {
            _client = client;
            _expectedText = expectedText;
            // Bound document reads while leaving room for the paste and nearby context.
            // 8192 mirrors the correction-learning service's maximum tracked text length.
            _readLength = Math.Clamp(expectedText.Length + 256, 512, 8192);
            _targetBusName = client.CurrentFocusedElement?.BusName;
            // Acquire the text-changed lease and subscribe here — before the caller sends Ctrl+V —
            // so the paste's text-changed can never fire unobserved; one that arrives before
            // WaitAsync can verify and latch in the TCS so the later await completes instantly.
            // The registry RegisterEvent this triggers is fire-and-forget: its propagation is fast
            // relative to the clipboard staging that follows the keystroke, and if the very first
            // event still races ahead of it, the watch degrades to the existing timeout fallback.
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
            // elapsed without a verified event — indeterminate, never false (some targets
            // do not emit text-changed or expose readable text).
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

        // The event source identifies the element to read; only its exact expected substring
        // can confirm. Unreadable or non-matching elements stay indeterminate, leaving the
        // watch armed for a later event or the timeout fallback.
        private async void OnTextChanged(AtSpiElementRef element)
        {
            try
            {
                if (
                    _targetBusName is not null
                    && !string.Equals(
                        element.BusName,
                        _targetBusName,
                        StringComparison.Ordinal
                    )
                )
                {
                    return;
                }

                // Never read a password (or role-unreadable) element — the same privacy
                // boundary every correction-learning read honors. Null fails closed: an
                // unknown role stays indeterminate rather than risk reading a password field.
                if (await _client.IsPasswordFieldAsync(element).ConfigureAwait(false) != false)
                {
                    return;
                }

                var currentText = await _client
                    .TryReadTextAsync(element, _readLength)
                    .ConfigureAwait(false);
                if (currentText?.Contains(_expectedText, StringComparison.Ordinal) == true)
                {
                    _textChanged.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                // Event handlers must never fault the AT-SPI dispatch path. The client's
                // text read already maps expected D-Bus failures to null; this is defense-in-depth.
                Trace.WriteLine(
                    $"[AtSpiPasteConfirmation] Failed to verify text-changed event: {ex.Message}"
                );
            }
        }
    }
}
