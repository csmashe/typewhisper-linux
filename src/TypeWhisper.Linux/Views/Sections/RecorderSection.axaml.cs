using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace TypeWhisper.Linux.Views.Sections;

public partial class RecorderSection : UserControl
{
    public RecorderSection()
    {
        InitializeComponent();
    }

    private async void OnCopyTranscript(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string transcript } || string.IsNullOrWhiteSpace(transcript))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
            await topLevel.Clipboard.SetTextAsync(transcript);
    }
}
