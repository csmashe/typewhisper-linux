using Avalonia.Controls;
using Avalonia.Input.Platform;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class ShortcutsSection : UserControl
{
    private ShortcutsSectionViewModel? _wired;

    public ShortcutsSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired is not null)
        {
            _wired.CopyCustomShortcutRequested -= OnCopyCustomShortcutRequested;
            _wired = null;
        }

        if (DataContext is ShortcutsSectionViewModel vm)
        {
            vm.CopyCustomShortcutRequested += OnCopyCustomShortcutRequested;
            _wired = vm;
        }
    }

    private async void OnCopyCustomShortcutRequested(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
            await topLevel.Clipboard.SetTextAsync(text);
    }
}
