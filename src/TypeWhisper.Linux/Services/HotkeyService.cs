using System.Diagnostics;
using SharpHook;
using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Global hotkey binding for the Linux shell. Uses SharpHook (libuiohook)
/// which works reliably on X11. Wayland support varies by compositor — a
/// future path is the XDG portal GlobalShortcuts protocol (GNOME 45+,
/// KDE Plasma 6+).
///
/// Three modes, matching the Windows shell:
///   - Toggle: press the hotkey to start recording, press again to stop.
///   - PushToTalk: hold the hotkey to record, release to stop.
///   - Hybrid: starts immediately on press. A short press stays active like
///     Toggle; holding past a threshold (600 ms) stops on release.
///
/// Callers subscribe to DictationStartRequested / DictationStopRequested /
/// DictationToggleRequested as they need. The orchestrator handles all
/// three with start-if-idle / stop-if-recording semantics.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int PushToTalkThresholdMs = 600;

    private readonly TaskPoolGlobalHook _hook = new();
    private readonly object _lock = new();

    private KeyCode _key = KeyCode.VcSpace;
    private ModifierMask _modifiers = ModifierMask.LeftCtrl | ModifierMask.LeftShift;
    private KeyCode? _promptPaletteKey;
    private ModifierMask _promptPaletteModifiers = ModifierMask.None;
    private RecordingMode _mode = RecordingMode.Toggle;
    private bool _keyIsDown;
    private DateTime _keyDownTime;
    private bool _running;
    private int _disposed;
    private Task? _hookTask;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler<string>? HookFailed;

    public RecordingMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public void Initialize()
    {
        lock (_lock)
        {
            if (_running || Volatile.Read(ref _disposed) == 1) return;
            _hook.KeyPressed += OnKeyPressed;
            _hook.KeyReleased += OnKeyReleased;
            _hookTask = _hook.RunAsync();
            _hookTask.ContinueWith(task =>
            {
                if (Volatile.Read(ref _disposed) == 1 || task.IsCanceled)
                    return;

                var error = task.Exception?.GetBaseException().Message ?? "Global hotkey hook stopped unexpectedly.";
                Trace.WriteLine($"[HotkeyService] Hook failed: {error}");
                HookFailed?.Invoke(this, error);
            }, TaskContinuationOptions.NotOnRanToCompletion);
            _running = true;
        }
    }

    public void SetHotkey(KeyCode key, ModifierMask modifiers)
    {
        _key = key;
        _modifiers = modifiers;
    }

    public void SetPromptPaletteHotkey(KeyCode? key, ModifierMask modifiers)
    {
        _promptPaletteKey = key;
        _promptPaletteModifiers = key is null ? ModifierMask.None : modifiers;
    }

    /// <summary>Human-friendly form of the currently-bound hotkey, e.g. "Ctrl+Shift+Space".</summary>
    public string CurrentHotkeyString => FormatHotkey(_key, _modifiers);
    public string CurrentPromptPaletteHotkeyString =>
        _promptPaletteKey is null ? "" : FormatHotkey(_promptPaletteKey.Value, _promptPaletteModifiers);

    /// <summary>
    /// Parses strings like "Ctrl+Shift+Space", "Alt+F9", "Ctrl+K" and binds
    /// them. Returns true on success. Accepts modifier tokens (Ctrl, Shift,
    /// Alt, Meta/Win/Super) and either a single letter, a digit, a function
    /// key (F1-F24), or a named key (Space, Enter, Tab, Escape, arrows, etc.).
    /// Invalid input leaves the current binding unchanged.
    /// </summary>
    public bool TrySetHotkeyFromString(string text)
    {
        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        // Don't let the dictation hotkey collide with the prompt palette — the
        // palette handler runs first in OnKeyPressed and would shadow this key.
        if (HotkeyMatches(key!.Value, modifiers, _promptPaletteKey, _promptPaletteModifiers))
            return false;

        SetHotkey(key.Value, modifiers);
        return true;
    }

    public bool TrySetPromptPaletteHotkeyFromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetPromptPaletteHotkey(null, ModifierMask.None);
            return true;
        }

        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        // Don't let the prompt palette collide with the dictation hotkey.
        if (HotkeyMatches(key!.Value, modifiers, _key, _modifiers))
            return false;

        SetPromptPaletteHotkey(key, modifiers);
        return true;
    }

    private static bool HotkeyMatches(KeyCode key, ModifierMask modifiers, KeyCode? otherKey, ModifierMask otherModifiers)
    {
        if (otherKey is null) return false;
        return key == otherKey.Value && modifiers == otherModifiers;
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

    private static bool TryParseHotkey(string text, out KeyCode? key, out ModifierMask modifiers)
    {
        key = null;
        modifiers = ModifierMask.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

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
                if (key is not null) return false;
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{char.ToUpperInvariant(part[0])}");
                continue;
            }

            if (part.Length == 1 && part[0] is >= '0' and <= '9')
            {
                if (key is not null) return false;
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
            if (named is not null)
            {
                if (key is not null) return false;
                key = named.Value;
                continue;
            }

            if (part.Length is >= 2 and <= 3 && part[0] == 'f' &&
                int.TryParse(part[1..], out var fNum) && fNum is >= 1 and <= 24)
            {
                if (key is not null) return false;
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"VcF{fNum}");
                continue;
            }

            key = null;
            return false;
        }

        return key is not null;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (MatchesPromptPaletteHotkey(e))
        {
            PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Data.KeyCode != _key) return;
        if (!ModifiersMatch(e.RawEvent.Mask, _modifiers)) return;

        // Ignore key-repeat: treat only the first press of a hold as the event.
        if (_keyIsDown) return;

        _keyIsDown = true;
        _keyDownTime = DateTime.UtcNow;

        try
        {
            switch (_mode)
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
            Trace.WriteLine($"[HotkeyService] Press handler threw: {ex.Message}");
        }
    }

    private bool MatchesPromptPaletteHotkey(KeyboardHookEventArgs e)
    {
        if (_promptPaletteKey is null) return false;
        return e.Data.KeyCode == _promptPaletteKey.Value
            && ModifiersMatch(e.RawEvent.Mask, _promptPaletteModifiers);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        // We only care about the release of the main key; modifier releases
        // are ignored so the user can let go of Ctrl/Shift first.
        if (e.Data.KeyCode != _key) return;
        if (!_keyIsDown) return;

        _keyIsDown = false;
        var heldMs = (DateTime.UtcNow - _keyDownTime).TotalMilliseconds;

        try
        {
            switch (_mode)
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
            Trace.WriteLine($"[HotkeyService] Release handler threw: {ex.Message}");
        }
    }

    internal static bool ModifiersMatch(ModifierMask pressed, ModifierMask required)
    {
        if (RequiresCtrl(required) && !HasCtrl(pressed))
            return false;

        if (RequiresShift(required) && !HasShift(pressed))
            return false;

        if (RequiresAlt(required) && !HasAlt(pressed))
            return false;

        if (RequiresMeta(required) && !HasMeta(pressed))
            return false;

        return true;
    }

    private static bool RequiresCtrl(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftCtrl) || mask.HasFlag(ModifierMask.RightCtrl);

    private static bool RequiresShift(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftShift) || mask.HasFlag(ModifierMask.RightShift);

    private static bool RequiresAlt(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftAlt) || mask.HasFlag(ModifierMask.RightAlt);

    private static bool RequiresMeta(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftMeta) || mask.HasFlag(ModifierMask.RightMeta);

    private static bool HasCtrl(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftCtrl) || mask.HasFlag(ModifierMask.RightCtrl);

    private static bool HasShift(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftShift) || mask.HasFlag(ModifierMask.RightShift);

    private static bool HasAlt(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftAlt) || mask.HasFlag(ModifierMask.RightAlt);

    private static bool HasMeta(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftMeta) || mask.HasFlag(ModifierMask.RightMeta);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _running = false;
        }

        var disposeTask = Task.Run(() =>
        {
            try { _hook.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[HotkeyService] Dispose threw: {ex.Message}"); }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
    }
}
