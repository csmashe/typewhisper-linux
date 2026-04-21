using System.Diagnostics;
using SharpHook;
using SharpHook.Native;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Minimal global hotkey binding for the Linux shell. Uses SharpHook
/// (libuiohook) which works reliably on X11. Wayland support varies by
/// compositor — for robust Wayland, a future path is the XDG portal
/// GlobalShortcuts protocol (GNOME 45+/KDE Plasma 6+).
///
/// v1 scope: one hotkey that toggles dictation start/stop. The richer
/// modes from the Windows HotkeyService (push-to-talk, profile-specific
/// hotkeys, cancel shortcut) are deferred.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly TaskPoolGlobalHook _hook = new();
    private readonly object _lock = new();

    private KeyCode _key = KeyCode.VcSpace;
    private ModifierMask _modifiers = ModifierMask.LeftCtrl | ModifierMask.LeftShift;
    private bool _running;
    private bool _disposed;

    public event EventHandler? DictationToggleRequested;

    public void Initialize()
    {
        lock (_lock)
        {
            if (_running || _disposed) return;
            _hook.KeyPressed += OnKeyPressed;
            _ = _hook.RunAsync(); // fire-and-forget; ex surfaces via TaskScheduler.UnobservedTaskException
            _running = true;
        }
    }

    public void SetHotkey(KeyCode key, ModifierMask modifiers)
    {
        _key = key;
        _modifiers = modifiers;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode != _key) return;
        if ((e.RawEvent.Mask & _modifiers) != _modifiers) return;

        try
        {
            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyService] Handler threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.Dispose();
            _running = false;
        }
    }
}
