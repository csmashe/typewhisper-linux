using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using System.Diagnostics;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class ShortcutsSection : UserControl
{
    // Tracks the currently-subscribed view model so the handler can be
    // unsubscribed before a new DataContext is wired in.
    private ShortcutsSectionViewModel? _wired;
    private Key? _pendingModifierKey;

    public ShortcutsSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        // Re-probe keyboard access and managed desktop-integration state whenever this
        // section is shown. Both checks are read-only; writes remain explicit actions.
        AttachedToVisualTree += (_, _) =>
            (DataContext as ShortcutsSectionViewModel)?.RefreshSectionState();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShortcutsSectionViewModel vm || !vm.IsCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        if (IsModifierKey(e.Key))
        {
            _pendingModifierKey = e.Key;
            return;
        }

        var keyName = GetHotkeyKeyName(e.Key);
        if (keyName is null)
        {
            return;
        }

        _pendingModifierKey = null;
        var parts = new List<string>(4);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }
        parts.Add(keyName);
        _ = vm.ApplyCapturedHotkeyAsync(string.Join('+', parts));
    }

    private void OnStartHotkeyCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShortcutsSectionViewModel vm)
        {
            vm.StartHotkeyCaptureCommand.Execute(null);
            Focus();
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (
            DataContext is not ShortcutsSectionViewModel vm
            || !vm.IsCapturingHotkey
            || _pendingModifierKey != e.Key
        )
        {
            return;
        }

        var keyName = GetSingleModifierName(e.Key);
        if (keyName is null)
        {
            return;
        }

        e.Handled = true;
        _pendingModifierKey = null;
        _ = vm.ApplyCapturedHotkeyAsync(keyName);
    }

    private static bool IsModifierKey(Key key) => GetSingleModifierName(key) is not null;

    private static string? GetSingleModifierName(Key key) => key switch
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

    private static string? GetHotkeyKeyName(Key key)
    {
        var name = key.ToString();
        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            return name;
        }
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1..];
        }
        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return name;
        }
        if (name.StartsWith("NumPad", StringComparison.Ordinal))
        {
            return name;
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.OemTilde => "BackQuote",
            Key.OemMinus => "Minus",
            Key.OemPlus => "Equals",
            Key.OemOpenBrackets => "OpenBracket",
            Key.OemCloseBrackets => "CloseBracket",
            Key.OemPipe => "Backslash",
            Key.OemSemicolon => "Semicolon",
            Key.OemQuotes => "Quote",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.OemQuestion => "Slash",
            Key.Add => "NumPadAdd",
            Key.Subtract => "NumPadSubtract",
            Key.Multiply => "NumPadMultiply",
            Key.Divide => "NumPadDivide",
            Key.Decimal => "NumPadDecimal",
            _ => name == "None" ? null : name,
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Clipboard access requires a TopLevel reference that only the view has,
        // so the ViewModel raises an event and the view performs the actual copy.
        if (_wired is not null)
        {
            _wired.CopyCustomShortcutRequested -= OnCopyCustomShortcutRequested;
            _wired = null;
        }

        // ReSharper disable once InvertIf — pattern variable `vm` is used in the block;
        // inverting would orphan the binding.
        if (DataContext is ShortcutsSectionViewModel vm)
        {
            vm.CopyCustomShortcutRequested += OnCopyCustomShortcutRequested;
            _wired = vm;
        }
    }

    // ReSharper disable once AsyncVoidMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnCopyCustomShortcutRequested(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            return;
        }

        try
        {
            await topLevel.Clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShortcutsSection] Copy to clipboard failed: {ex.Message}");
        }
    }
}
