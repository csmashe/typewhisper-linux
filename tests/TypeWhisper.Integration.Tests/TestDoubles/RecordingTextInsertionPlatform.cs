using TypeWhisper.Linux.Services;

namespace TypeWhisper.Integration.Tests.TestDoubles;

internal sealed class RecordingTextInsertionPlatform : ITextInsertionPlatform
{
    private readonly Lock _gate = new();
    private readonly List<string> _typed = [];
    private readonly List<string> _clipboardWrites = [];

    public bool IsClipboardSetAvailable => true;
    public bool IsPasteAvailable => true;
    public bool IsKdePlasma => false;
    public bool PrefersDirectTypingForUnknownTarget => true;
    public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
    public bool LastTypingDeliveredPartialText => false;

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

    public Task<bool> TypeTextAsync(string text)
    {
        lock (_gate)
        {
            _typed.Add(text);
        }

        return Task.FromResult(true);
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
