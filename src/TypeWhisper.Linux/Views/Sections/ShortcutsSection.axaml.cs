using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using System.Diagnostics;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class ShortcutsSection : UserControl
{
    // Tracks the currently-subscribed view model so the handler can be
    // unsubscribed before a new DataContext is wired in.
    private ShortcutsSectionViewModel? _wired;
    private HotkeyChordRecorder _recorder = new();
    private Window? _window;

    public ShortcutsSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnCaptureKeyUp, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => CancelCapture();
        DetachedFromVisualTree += (_, _) =>
        {
            CancelCapture();
            if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
            _window = null;
            UnwireViewModel();
        };
        // Re-probe keyboard access and managed desktop-integration state whenever this
        // section is shown. Both checks are read-only; writes remain explicit actions.
        AttachedToVisualTree += (_, _) =>
        {
            WireViewModel();
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Deactivated += OnWindowDeactivated;
            (DataContext as ShortcutsSectionViewModel)?.RefreshSectionState();
        };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelCapture();

    private void CancelCapture()
    {
        _wired?.CancelHotkeyCaptureCommand.Execute(null);
        _recorder = new();
    }

    private void OnStartHotkeyCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_wired?.CanStartHotkeyCapture != true) return;
        _recorder = new();
        Focus();
        _wired.StartHotkeyCaptureCommand.Execute(null);
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e) => CaptureKey(e, true);
    private void OnCaptureKeyUp(object? sender, KeyEventArgs e) => CaptureKey(e, false);

    private async void CaptureKey(KeyEventArgs e, bool pressed)
    {
        if (_wired is not { IsCapturingHotkey: true } vm) return;
        e.Handled = true;
        var result = _recorder.Handle(e.Key, e.KeyModifiers, pressed);
        if (result.Cancelled) CancelCapture();
        else if (result.Unsupported) vm.ReportUnsupportedHotkey();
        else if (result.Chord is { } chord)
        {
            try { await vm.ApplyCapturedHotkeyAsync(chord); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ShortcutsSection] Failed to apply captured hotkey: {ex}");
                CancelCapture();
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnwireViewModel();
        if (TopLevel.GetTopLevel(this) is not null) WireViewModel();
    }

    private void UnwireViewModel()
    {
        // Clipboard access requires a TopLevel reference that only the view has,
        // so the ViewModel raises an event and the view performs the actual copy.
        if (_wired is not null)
        {
            CancelCapture();
            _wired.CopyCustomShortcutRequested -= OnCopyCustomShortcutRequested;
            _wired = null;
        }
    }

    private void WireViewModel()
    {
        if (_wired is not null) return;
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
