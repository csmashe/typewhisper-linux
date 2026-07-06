using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace TypeWhisper.Linux.Views.Sections;

public partial class RecorderSection : UserControl
{
    public RecorderSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnCopyTranscript(object? sender, RoutedEventArgs e)
    {
        if (
            sender is not Button { Tag: string transcript }
            || string.IsNullOrWhiteSpace(transcript)
        )
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
        {
            await topLevel.Clipboard.SetTextAsync(transcript);
        }
    }
}