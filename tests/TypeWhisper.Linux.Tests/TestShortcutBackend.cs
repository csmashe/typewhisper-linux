using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;

namespace TypeWhisper.Linux.Tests;

internal sealed class TestShortcutBackend : IGlobalShortcutBackend
{
    private readonly TaskCompletionSource _gate = new();
    private int _pending;
    private int _registerCount;

    public GlobalShortcutRegistrationResult NextResult { get; init; } =
        new(
            true,
            "test",
            null,
            false,
            null
        );

    // HotkeyService applies through chained thread-pool continuations, so the increment must be
    // atomic and the read must not see a stale cached value.
    public int RegisterCount => Volatile.Read(ref _registerCount);

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

    public event EventHandler? DictationToggleRequested;

    public event EventHandler? DictationStartRequested;

    public event EventHandler? DictationStopRequested;

    public event EventHandler? DictationDiscardRequested;

    public event EventHandler? PromptPaletteRequested;

    public event EventHandler? TransformSelectionRequested;

    public event EventHandler? RecentTranscriptionsRequested;

    public event EventHandler? CopyLastTranscriptionRequested;

    public event EventHandler? ReadLastTranscriptionRequested;

    public event EventHandler? PasteLastTranscriptionRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler<string>? PromptActionRequested;

    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested;

    public event EventHandler? ProfileDictationStopRequested;

    public event EventHandler<string>? ProfileTextProcessingRequested;

    public event EventHandler<string>? Failed { add { } remove { } }

    public static HotkeyService CreateHotkeyService()
    {
        return new HotkeyService(new BackendSelector(static () => new TestShortcutBackend()));
    }

    public void RaisePromptAction(string actionId)
    {
        PromptActionRequested?.Invoke(this, actionId);
    }

    public void RaiseCaptureTestEvents()
    {
        DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        DictationStartRequested?.Invoke(this, EventArgs.Empty);
        PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
        RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
        CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        ReadLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        PasteLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
        ProfileDictationStartRequested?.Invoke(this, "profile");
        ProfileDictationToggleRequested?.Invoke(this, "profile");
        ProfileTextProcessingRequested?.Invoke(this, "profile");
        PromptActionRequested?.Invoke(this, "action");
        DictationStopRequested?.Invoke(this, EventArgs.Empty);
        DictationDiscardRequested?.Invoke(this, EventArgs.Empty);
        CancelRequested?.Invoke(this, EventArgs.Empty);
        ProfileDictationStopRequested?.Invoke(this, EventArgs.Empty);
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
        Interlocked.Increment(ref _registerCount);
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
