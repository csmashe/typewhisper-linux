using TypeWhisper.Linux.Services;

namespace TypeWhisper.Integration.Tests.TestDoubles;

internal sealed class RecordingTextInsertionPlatform : ITextInsertionPlatform
{
    private readonly Lock _gate = new();
    private readonly List<string> _typed = [];
    private readonly List<string> _clipboardWrites = [];
    private readonly Queue<(bool Succeeds, bool DeliveredPartial)> _scriptedTyping = new();
    private bool _lastTypingDeliveredPartialText;

    public bool IsClipboardSetAvailable => true;
    public bool IsPasteAvailable => true;
    public bool IsKdePlasma => false;
    public bool PrefersDirectTypingForUnknownTarget => true;
    public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;

    public bool LastTypingDeliveredPartialText
    {
        get
        {
            lock (_gate)
            {
                return _lastTypingDeliveredPartialText;
            }
        }
    }

    internal IReadOnlyList<string> Typed
    {
        get
        {
            lock (_gate)
            {
                return _typed.ToArray();
            }
        }
    }

    internal IReadOnlyList<string> ClipboardWrites
    {
        get
        {
            lock (_gate)
            {
                return _clipboardWrites.ToArray();
            }
        }
    }

    public Task<string?> TryGetClipboardTextAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task<bool> SetClipboardTextAsync(string text)
    {
        lock (_gate)
        {
            _clipboardWrites.Add(text);
        }

        return Task.FromResult(true);
    }

    public Task<bool> ClipboardHasNonTextFormatsAsync()
    {
        return Task.FromResult(false);
    }

    public Task DelayAsync(TimeSpan delay)
    {
        return Task.CompletedTask;
    }

    public string? GetActiveWindowId()
    {
        return null;
    }

    public Task<bool> ActivateWindowAsync(string windowId)
    {
        return Task.FromResult(true);
    }

    public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
    {
        return Task.FromResult(true);
    }

    // Scripts the outcome of one upcoming TypeTextAsync call, in enqueue order. Calls past the
    // scripted ones (and every call when nothing is scripted) succeed and deliver nothing partial.
    internal void EnqueueTypingOutcome(bool succeeds, bool deliveredPartial)
    {
        lock (_gate)
        {
            _scriptedTyping.Enqueue((succeeds, deliveredPartial));
        }
    }

    public Task<bool> TypeTextAsync(string text)
    {
        lock (_gate)
        {
            _typed.Add(text);
            var outcome = _scriptedTyping.Count > 0
                ? _scriptedTyping.Dequeue()
                : (Succeeds: true, DeliveredPartial: false);
            _lastTypingDeliveredPartialText = outcome.DeliveredPartial;
            return Task.FromResult(outcome.Succeeds);
        }
    }

    public Task<bool> SendCopyAsync(bool useTerminalShortcut)
    {
        return Task.FromResult(true);
    }

    public Task<bool> SendEnterAsync()
    {
        return Task.FromResult(true);
    }
}
