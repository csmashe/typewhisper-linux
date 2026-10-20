using Avalonia.Input;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>Builds one chord and completes only after every captured key is released.</summary>
internal sealed class HotkeyChordRecorder
{
    private readonly HashSet<Key> _held = [];
    private readonly HashSet<Key> _modifiers = [];
    private string? _chord;
    private bool _hadNonModifier;

    public CaptureResult Handle(Key key, KeyModifiers modifiers, bool pressed)
    {
        if (pressed)
        {
            if (key == Key.Escape && modifiers == KeyModifiers.None)
                return new(Cancelled: true);
            if (!_held.Add(key)) return default;
            if (ModifierName(key) is not null)
            {
                _modifiers.Add(key);
                return default;
            }
            _hadNonModifier = true;
            var name = KeyName(key);
            if (name is null) return new(Unsupported: true);
            if (_chord is not null) return default;
            var parts = new List<string>();
            if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
            parts.Add(name);
            _chord = string.Join('+', parts);
            return default;
        }

        if (!_held.Remove(key) || _held.Count != 0) return default;
        var chord = _chord;
        if (chord is null && !_hadNonModifier && _modifiers.Count == 1)
            chord = ModifierName(_modifiers.Single());
        _chord = null;
        _modifiers.Clear();
        _hadNonModifier = false;
        return new(Chord: chord);
    }

    private static string? ModifierName(Key key) => key switch
    {
        Key.LeftCtrl => "Left Ctrl",
        Key.RightCtrl => "Right Ctrl",
        Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift",
        Key.LeftAlt => "Left Alt",
        Key.RightAlt => "Right Alt",
        Key.LWin => "Left Meta",
        Key.RWin => "Right Meta",
        _ => null,
    };

    // Restrict capture to the existing common backend/desktop-writer key set.
    // Punctuation and numpad keys need explicit keysym support before they can be recorded.
    private static string? KeyName(Key key)
    {
        var name = key.ToString();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') return name;
        if (name.Length == 2 && name[0] == 'D' && name[1] is >= '0' and <= '9') return name[1..];
        if (name.StartsWith('F') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return name;
        return key switch
        {
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab",
            Key.Escape => "Escape", Key.Back => "Backspace", Key.Delete => "Delete",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            _ => null,
        };
    }
}

internal readonly record struct CaptureResult(string? Chord = null, bool Cancelled = false, bool Unsupported = false);
