using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class ShortcutsSection : UserControl
{
    // Tracks the currently-subscribed view model so the handler can be
    // unsubscribed before a new DataContext is wired in.
    private ShortcutsSectionViewModel? _wired;

    public ShortcutsSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Re-probe keyboard access whenever this section is shown, so the no-access
        // banner / compositor fallback reflect access granted since the (eagerly
        // constructed) view model was built — e.g. via first-run onboarding.
        AttachedToVisualTree += (_, _) =>
            (DataContext as ShortcutsSectionViewModel)?.RefreshKeyboardAccess();
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