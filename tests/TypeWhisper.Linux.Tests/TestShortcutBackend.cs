using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;

namespace TypeWhisper.Linux.Tests;

internal sealed class TestShortcutBackend : IGlobalShortcutBackend
{
    private readonly TaskCompletionSource _gate = new();
    private int _pending;

    public GlobalShortcutRegistrationResult NextResult { get; init; } =
        new(
            true,
            "test",
            null,
            false,
            null
        );

    public int RegisterCount { get; private set; }
    public GlobalShortcutSet? LastSet { get; private set; }
    public bool Disposed { get; private set; }

    public string Id => "test";
    public string DisplayName => "Test";
    public bool SupportsPressRelease => true;
    public bool IsGlobalScope => true;

    public bool IsAvailable()
    {
        return true;
    }

    public event EventHandler? DictationToggleRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? DictationStartRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? DictationStopRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? DictationDiscardRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PromptPaletteRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? TransformSelectionRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? RecentTranscriptionsRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? CopyLastTranscriptionRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? CancelRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? PromptActionRequested;

    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested { add { } remove { } }

    public event EventHandler? ProfileDictationStopRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? ProfileTextProcessingRequested;

    public event EventHandler<string>? Failed
    {
        add { }
        remove { }
    }

    public static HotkeyService CreateHotkeyService()
    {
        return new HotkeyService(new BackendSelector(static () => new TestShortcutBackend()));
    }

    public void RaisePromptAction(string actionId)
    {
        PromptActionRequested?.Invoke(this, actionId);
    }

    public void RaiseProfileDictationToggle(string profileId)
    {
        ProfileDictationToggleRequested?.Invoke(this, profileId);
    }

    public void RaiseProfileTextProcessing(string profileId)
    {
        ProfileTextProcessingRequested?.Invoke(this, profileId);
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        _gate.TrySetResult();
        Interlocked.Increment(ref _pending);
        RegisterCount++;
        LastSet = shortcuts;
        Interlocked.Decrement(ref _pending);
        return Task.FromResult(NextResult);
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public async Task WaitUntilSettledAsync()
    {
        // Spin briefly to let the coordinator's chained continuations
        // drain — they run on the thread-pool scheduler so a yield is
        // enough in normal cases; a short timeout guards against hangs.
        await Task.WhenAny(_gate.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _pending) == 0)
            {
                await Task.Delay(20);
                if (Volatile.Read(ref _pending) == 0)
                {
                    return;
                }
            }

            await Task.Delay(10);
        }
    }
}
