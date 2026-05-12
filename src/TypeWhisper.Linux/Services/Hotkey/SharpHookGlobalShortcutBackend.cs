using System.Diagnostics;
using SharpHook;
using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// SharpHook-backed implementation. Works reliably on X11; on Wayland the
/// hook only receives events while the application owns focus — that's the
/// gap Phase 2's evdev backend closes.
///
/// Owns the OS-level libuiohook lifecycle, the repeat-guard flags, and the
/// key-down timing used by the Hybrid mode threshold.
/// </summary>
public sealed class SharpHookGlobalShortcutBackend : IGlobalShortcutBackend
{
    private const int PushToTalkThresholdMs = 600;
    public const string BackendId = "linux-sharphook";

    private readonly TaskPoolGlobalHook _hook = new();
    private readonly object _lock = new();

    private GlobalShortcutSet? _shortcuts;
    private bool _running;
    private int _disposed;
    private Task? _hookTask;

    // Repeat-guard flags so OS auto-repeat doesn't spam events while a key is
    // held down. Cleared on the matching key release.
    private bool _dictationKeyDown;
    private bool _promptKeyDown;
    private bool _recentKeyDown;
    private bool _copyLastKeyDown;
    private bool _transformSelectionKeyDown;
    private bool _cancelKeyDown;
    private DateTime _dictationKeyDownTime;

    public string Id => BackendId;
    public string DisplayName => "SharpHook (libuiohook)";
    public bool SupportsPressRelease => true;
    public bool IsAvailable() => true;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? Failed;

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        lock (_lock)
        {
            _shortcuts = shortcuts;

            if (!_running && Volatile.Read(ref _disposed) == 0)
            {
                _hook.KeyPressed += OnKeyPressed;
                _hook.KeyReleased += OnKeyReleased;
                _hookTask = _hook.RunAsync();
                _hookTask.ContinueWith(task =>
                {
                    if (Volatile.Read(ref _disposed) == 1 || task.IsCanceled)
                        return;

                    var error = task.Exception?.GetBaseException().Message
                        ?? "Global hotkey hook stopped unexpectedly.";
                    Trace.WriteLine($"[SharpHookBackend] Hook failed: {error}");
                    Failed?.Invoke(this, error);
                }, TaskContinuationOptions.NotOnRanToCompletion);
                _running = true;
            }
        }

        return Task.FromResult(new GlobalShortcutRegistrationResult(
            Success: true,
            BackendId: BackendId,
            UserMessage: null,
            RequiresToggleMode: false,
            TroubleshootingCommand: null));
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _shortcuts = null;
        }
        return Task.CompletedTask;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var set = Volatile.Read(ref _shortcuts);
        if (set is null) return;

        var key = e.Data.KeyCode;
        var mods = e.RawEvent.Mask;
        var match = ShortcutMatcher.Match(key, mods, set);

        // Cancel: only fires while a dictation is active and only when it
        // doesn't collide with another binding — otherwise the regular
        // matcher handles the press below.
        if (match == ShortcutMatchKind.Cancel)
        {
            if (!_cancelKeyDown)
            {
                _cancelKeyDown = true;
                if (set.IsCancelEnabled && !ShortcutMatcher.CancelCollidesWithAnyBinding(set))
                {
                    try { CancelRequested?.Invoke(this, EventArgs.Empty); }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[SharpHookBackend] Cancel handler threw: {ex.Message}");
                    }
                    return;
                }
            }
            // Fall through: cancel collides with a binding — let that binding
            // handle the press as if cancel didn't exist.
            match = ShortcutMatcher.Match(key, mods, set with { CancelKey = KeyCode.VcUndefined });
        }

        switch (match)
        {
            case ShortcutMatchKind.RecentTranscriptions:
                if (_recentKeyDown) return;
                _recentKeyDown = true;
                RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
                return;

            case ShortcutMatchKind.CopyLastTranscription:
                if (_copyLastKeyDown) return;
                _copyLastKeyDown = true;
                CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
                return;

            case ShortcutMatchKind.TransformSelection:
                if (_transformSelectionKeyDown) return;
                _transformSelectionKeyDown = true;
                TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
                return;

            case ShortcutMatchKind.PromptPalette:
                if (_promptKeyDown) return;
                _promptKeyDown = true;
                PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
                return;

            case ShortcutMatchKind.Dictation:
                if (_dictationKeyDown) return;
                _dictationKeyDown = true;
                _dictationKeyDownTime = DateTime.UtcNow;
                try
                {
                    switch (set.Mode)
                    {
                        case RecordingMode.Toggle:
                            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
                            break;
                        case RecordingMode.PushToTalk:
                            DictationStartRequested?.Invoke(this, EventArgs.Empty);
                            break;
                        case RecordingMode.Hybrid:
                            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[SharpHookBackend] Press handler threw: {ex.Message}");
                }
                return;
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var set = Volatile.Read(ref _shortcuts);
        if (set is null) return;

        var key = e.Data.KeyCode;

        // Clear repeat-guards on the matching key release.
        if (set.PromptPaletteKey is not null && key == set.PromptPaletteKey.Value)
            _promptKeyDown = false;
        if (set.RecentTranscriptionsKey is not null && key == set.RecentTranscriptionsKey.Value)
            _recentKeyDown = false;
        if (set.CopyLastTranscriptionKey is not null && key == set.CopyLastTranscriptionKey.Value)
            _copyLastKeyDown = false;
        if (set.TransformSelectionKey is not null && key == set.TransformSelectionKey.Value)
            _transformSelectionKeyDown = false;
        if (key == set.CancelKey)
            _cancelKeyDown = false;

        // Modifier-only releases are ignored so the user can let go of
        // Ctrl/Shift first; only the main key release closes the press.
        if (key != set.DictationKey) return;
        if (!_dictationKeyDown) return;

        _dictationKeyDown = false;
        var heldMs = (DateTime.UtcNow - _dictationKeyDownTime).TotalMilliseconds;

        try
        {
            switch (set.Mode)
            {
                case RecordingMode.PushToTalk:
                    DictationStopRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case RecordingMode.Hybrid:
                    if (heldMs >= PushToTalkThresholdMs)
                        DictationStopRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case RecordingMode.Toggle:
                    // No-op — Toggle handled on press.
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SharpHookBackend] Release handler threw: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _running = false;
        }

        var disposeTask = Task.Run(() =>
        {
            try { _hook.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[SharpHookBackend] Dispose threw: {ex.Message}"); }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
        return ValueTask.CompletedTask;
    }
}
