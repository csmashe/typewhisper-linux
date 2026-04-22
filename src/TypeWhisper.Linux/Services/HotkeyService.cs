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
///   - Hybrid: a short press acts as Toggle; holding past a threshold
///     (600 ms) switches to push-to-talk semantics and releasing stops.
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
    private CancellationTokenSource? _hybridStartCts;
    private bool _hybridDidStart;
    private bool _running;
    private bool _disposed;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;

    public RecordingMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public void Initialize()
    {
        lock (_lock)
        {
            if (_running || _disposed) return;
            _hook.KeyPressed += OnKeyPressed;
            _hook.KeyReleased += OnKeyReleased;
            _ = _hook.RunAsync();
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

        SetHotkey(key!.Value, modifiers);
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

        SetPromptPaletteHotkey(key, modifiers);
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
            if (named is not null)
            {
                key = named.Value;
                continue;
            }

            if (part.Length is >= 2 and <= 3 && part[0] == 'f' &&
                int.TryParse(part[1..], out var fNum) && fNum is >= 1 and <= 24)
            {
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
        if ((e.RawEvent.Mask & _modifiers) != _modifiers) return;

        // Ignore key-repeat: treat only the first press of a hold as the event.
        if (_keyIsDown) return;

        _keyIsDown = true;
        _keyDownTime = DateTime.UtcNow;
        _hybridDidStart = false;

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
                    QueueHybridStartAfterThreshold();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyService] Press handler threw: {ex.Message}");
        }
    }

    private bool MatchesPromptPaletteHotkey(KeyboardHookEventArgs e)
    {
        if (_promptPaletteKey is null) return false;
        return e.Data.KeyCode == _promptPaletteKey.Value
            && (e.RawEvent.Mask & _promptPaletteModifiers) == _promptPaletteModifiers;
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
                    CancelHybridStart();
                    if (_hybridDidStart && heldMs >= PushToTalkThresholdMs)
                        DictationStopRequested?.Invoke(this, EventArgs.Empty);
                    else if (!_hybridDidStart)
                        DictationToggleRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case RecordingMode.Toggle:
                    // No-op — Toggle handled on press.
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyService] Release handler threw: {ex.Message}");
        }
    }

    private void QueueHybridStartAfterThreshold()
    {
        CancelHybridStart();
        var cts = new CancellationTokenSource();
        _hybridStartCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PushToTalkThresholdMs, cts.Token);
                if (cts.IsCancellationRequested || !_keyIsDown || _mode != RecordingMode.Hybrid)
                    return;

                _hybridDidStart = true;
                DictationStartRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Quick-tap path: release canceled the delayed start.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyService] Hybrid start timer threw: {ex.Message}");
            }
        });
    }

    private void CancelHybridStart()
    {
        try { _hybridStartCts?.Cancel(); } catch { /* ignore */ }
        _hybridStartCts?.Dispose();
        _hybridStartCts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelHybridStart();
        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _running = false;
        }

        var disposeTask = Task.Run(() =>
        {
            try { _hook.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[HotkeyService] Dispose threw: {ex.Message}"); }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
    }
}
