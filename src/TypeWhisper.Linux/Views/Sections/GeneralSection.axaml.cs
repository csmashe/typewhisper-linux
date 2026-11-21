using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class GeneralSection : UserControl
{
    public GeneralSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnCopyExample(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CommandExample example }
            || DataContext is not GeneralSectionViewModel viewModel)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var copied = false;
        if (clipboard is not null)
        {
            try
            {
                await clipboard.SetTextAsync(example.Command);
                copied = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GeneralSection] Copy to clipboard failed: {ex.Message}");
            }
        }

        viewModel.ReportExampleCopied(example, copied);
    }
}
