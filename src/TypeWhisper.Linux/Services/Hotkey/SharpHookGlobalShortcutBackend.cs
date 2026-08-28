using SharpHook;
using SharpHook.Native;
using System.Diagnostics;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     SharpHook-backed implementation. Works globally on X11; on Wayland only
///     receives events while the app owns focus (the evdev backend fills that gap).
///     Delegates chord state to <see cref="ShortcutDispatcher" /> so behaviour
///     is identical across SharpHook and evdev.
/// </summary>
public sealed class SharpHookGlobalShortcutBackend : IGlobalShortcutBackend
{
    private const string BackendId = "linux-sharphook";
    private readonly ShortcutDispatcher _dispatcher = new();

    private readonly TaskPoolGlobalHook _hook = new();
    private readonly Lock _lock = new();
    private int _disposed;
    private Task? _hookTask;

    private bool _running;

    public SharpHookGlobalShortcutBackend()
    {
        _dispatcher.DictationToggleRequested += () =>
            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStartRequested += () =>
            DictationStartRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStopRequested += () =>
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptPaletteRequested += () =>
            PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.TransformSelectionRequested += () =>
            TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.RecentTranscriptionsRequested += () =>
            RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CopyLastTranscriptionRequested += () =>
            CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CancelRequested += () => CancelRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptActionRequested += actionId =>
            PromptActionRequested?.Invoke(this, actionId);
        _dispatcher.ProfileDictationToggleRequested += id =>
            ProfileDictationToggleRequested?.Invoke(this, id);
        _dispatcher.ProfileDictationStartRequested += id =>
            ProfileDictationStartRequested?.Invoke(this, id);
        _dispatcher.ProfileDictationStopRequested += () =>
            ProfileDictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.ProfileTextProcessingRequested += id =>
            ProfileTextProcessingRequested?.Invoke(this, id);
    }

    public string Id => BackendId;
    public string DisplayName => "SharpHook (libuiohook)";
    public bool SupportsPressRelease => true;

    /// <summary>
    ///     Global on X11; focus-only on Wayland. Reported honestly so the status
    ///     panel doesn't mislead Wayland users.
    /// </summary>
    public bool IsGlobalScope => !WaylandSessionDetector.IsWaylandSession();

    public bool IsAvailable()
    {
        return true;
    }

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;

    // SharpHook has no session gating, so it never discards; satisfy the interface with a no-op.
    public event EventHandler? DictationDiscardRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? PromptActionRequested;
    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested;
    public event EventHandler? ProfileDictationStopRequested;
    public event EventHandler<string>? ProfileTextProcessingRequested;
    public event EventHandler<string>? Failed;

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        _dispatcher.UpdateShortcuts(shortcuts);

        lock (_lock)
        {
            // ReSharper disable once InvertIf — last statement in the lock; inverting would
            // duplicate the trailing success-result construction with no clean early exit.
            if (!_running && Volatile.Read(ref _disposed) == 0)
            {
                _hook.KeyPressed += OnKeyPressed;
                _hook.KeyReleased += OnKeyReleased;
                _hookTask = _hook.RunAsync();
                _hookTask.ContinueWith(
                    task =>
                    {
                        if (Volatile.Read(ref _disposed) == 1 || task.IsCanceled)
                        {
                            return;
                        }

                        lock (_lock)
                        {
                            _running = false;
                        }

                        var error =
                            task.Exception?.GetBaseException().Message
                            ?? "Global hotkey hook stopped unexpectedly.";
                        Trace.WriteLine($"[SharpHookBackend] Hook failed: {error}");
                        Failed?.Invoke(this, error);
                    },
                    TaskContinuationOptions.NotOnRanToCompletion
                );
                _running = true;
            }
        }

        return Task.FromResult(
            new GlobalShortcutRegistrationResult(
                true,
                BackendId,
                null,
                false,
                null
            )
        );
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        _dispatcher.ClearShortcuts();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _running = false;
        }

        // libuiohook's Dispose blocks until the hook thread stops; run it off
        // the caller's thread. 1-second timeout prevents hanging on exit.
        var disposeTask = Task.Run(() =>
        {
            try
            {
                _hook.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SharpHookBackend] Dispose threw: {ex.Message}");
            }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
        return ValueTask.CompletedTask;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        _dispatcher.Handle(e.Data.KeyCode, NormalizeMask(e.Data.KeyCode, e.RawEvent.Mask), true);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        _dispatcher.Handle(e.Data.KeyCode, NormalizeMask(e.Data.KeyCode, e.RawEvent.Mask), false);
    }

    // libuiohook may include a modifier key's own bit in the mask on press.
    // Strip it so a bare "Right Ctrl" matches a (VcRightControl, None) binding —
    // mirrors the same strip in EvdevGlobalShortcutBackend.OnKeyEvent.
    private static ModifierMask NormalizeMask(KeyCode key, ModifierMask mask)
    {
        var modBit = key switch
        {
            KeyCode.VcLeftControl => ModifierMask.LeftCtrl,
            KeyCode.VcRightControl => ModifierMask.RightCtrl,
            KeyCode.VcLeftShift => ModifierMask.LeftShift,
            KeyCode.VcRightShift => ModifierMask.RightShift,
            KeyCode.VcLeftAlt => ModifierMask.LeftAlt,
            KeyCode.VcRightAlt => ModifierMask.RightAlt,
            KeyCode.VcLeftMeta => ModifierMask.LeftMeta,
            KeyCode.VcRightMeta => ModifierMask.RightMeta,
            _ => ModifierMask.None,
        };
        return modBit == ModifierMask.None ? mask : mask & ~modBit;
    }
}
