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

    /// <summary>Human-friendly form of the currently-bound hotkey, e.g. "Ctrl+Shift+Space".</summary>
    public string CurrentHotkeyString => FormatHotkey(_key, _modifiers);

    /// <summary>
    /// Parses strings like "Ctrl+Shift+Space", "Alt+F9", "Ctrl+K" and binds
    /// them. Returns true on success. Accepts a small vocabulary of modifier
    /// tokens (Ctrl, Shift, Alt, Meta/Win) and either a single letter, a
    /// function key (F1-F24), or a small set of named keys (Space, Enter,
    /// Tab, Escape, etc.). Invalid input leaves the current binding unchanged.
    /// </summary>
    public bool TrySetHotkeyFromString(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = ModifierMask.None;
        KeyCode? key = null;

        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": modifiers |= ModifierMask.LeftCtrl; continue;
                case "shift": modifiers |= ModifierMask.LeftShift; continue;
                case "alt": modifiers |= ModifierMask.LeftAlt; continue;
                case "meta" or "super" or "win": modifiers |= ModifierMask.LeftMeta; continue;
            }

            if (part.Length == 1 && part[0] is >= 'a' and <= 'z')
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{char.ToUpperInvariant(part[0])}");
                continue;
            }
            if (part.Length == 1 && part[0] is >= '0' and <= '9')
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{part[0]}");
                continue;
            }

            var named = part switch
            {
                "space" => KeyCode.VcSpace,
                "enter" or "return" => KeyCode.VcEnter,
                "tab" => KeyCode.VcTab,
                "escape" or "esc" => KeyCode.VcEscape,
                "backspace" => KeyCode.VcBackspace,
                "delete" or "del" => KeyCode.VcDelete,
                "home" => KeyCode.VcHome,
                "end" => KeyCode.VcEnd,
                "pageup" => KeyCode.VcPageUp,
                "pagedown" => KeyCode.VcPageDown,
                "left" => KeyCode.VcLeft,
                "right" => KeyCode.VcRight,
                "up" => KeyCode.VcUp,
                "down" => KeyCode.VcDown,
                _ => (KeyCode?)null,
            };
            if (named is not null) { key = named.Value; continue; }

            // F1 – F24
            if (part.Length is >= 2 and <= 3 && part[0] == 'f' &&
                int.TryParse(part[1..], out var fNum) && fNum is >= 1 and <= 24)
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"VcF{fNum}");
                continue;
            }

            return false; // unknown token
        }

        if (key is null) return false;
        SetHotkey(key.Value, modifiers);
        return true;
    }

    private static string FormatHotkey(KeyCode key, ModifierMask mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierMask.LeftCtrl) || mods.HasFlag(ModifierMask.RightCtrl)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierMask.LeftShift) || mods.HasFlag(ModifierMask.RightShift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierMask.LeftAlt) || mods.HasFlag(ModifierMask.RightAlt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierMask.LeftMeta) || mods.HasFlag(ModifierMask.RightMeta)) parts.Add("Meta");

        var keyName = key.ToString();
        if (keyName.StartsWith("Vc", StringComparison.Ordinal)) keyName = keyName[2..];
        parts.Add(keyName);
        return string.Join('+', parts);
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
