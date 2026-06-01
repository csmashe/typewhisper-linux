using SharpHook;
using SharpHook.Native;
using System.Diagnostics;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     SharpHook-backed implementation. Works reliably on X11; on Wayland the
///     hook only receives events while the application owns focus — that's the
///     gap Phase 2's evdev backend closes.
///     Hands off the configured-chord state machine to a shared
///     <see cref="ShortcutDispatcher" /> so user-visible press/release/mode
///     behavior stays identical across SharpHook and evdev.
/// </summary>
public sealed class SharpHookGlobalShortcutBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-sharphook";
    private readonly ShortcutDispatcher _dispatcher = new();

    private readonly TaskPoolGlobalHook _hook = new();
    private readonly object _lock = new();
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
    }

    public string Id => BackendId;
    public string DisplayName => "SharpHook (libuiohook)";
    public bool SupportsPressRelease => true;

    /// <summary>
    ///     SharpHook hooks the X11 server (global) on X11 sessions but only
    ///     receives events while TypeWhisper owns the keyboard focus under
    ///     Wayland. Report scope honestly so the status panel doesn't mislead
    ///     Wayland users into thinking their hotkey works in any window.
    /// </summary>
    public bool IsGlobalScope =>
        !string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase
        );

    public bool IsAvailable()
    {
        return true;
    }

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? PromptActionRequested;
    public event EventHandler<string>? Failed;

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        _dispatcher.UpdateShortcuts(shortcuts);

        lock (_lock)
        {
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

        // libuiohook's Dispose blocks on the hook thread stopping; run it off
        // the caller's thread so DisposeAsync doesn't deadlock on shutdown.
        // The 1-second timeout is a safety net — we don't block the application
        // exit indefinitely if the hook thread hangs.
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

    // When the trigger key is itself a side-specific modifier, libuiohook's
    // mask may include the bit for that key on press (and lose it on release).
    // Mask it out so a bare "Right Ctrl" press matches a `(VcRightControl,
    // None)` binding regardless of platform — mirrors the equivalent strip
    // in EvdevGlobalShortcutBackend.OnKeyEvent.
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
            _ => ModifierMask.None
        };
        return modBit == ModifierMask.None ? mask : mask & ~modBit;
    }
}