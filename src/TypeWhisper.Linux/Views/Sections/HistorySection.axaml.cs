using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Views.Sections;

public partial class HistorySection : UserControl
{
    public HistorySection() => InitializeComponent();

    private async void OnCopyRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } || string.IsNullOrWhiteSpace(text))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
            await topLevel.Clipboard.SetTextAsync(text);
    }

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HistorySectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export history",
            SuggestedFileName = $"typewhisper-history-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text")
                {
                    Patterns = ["*.txt"]
                },
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"]
                },
                new FilePickerFileType("Markdown")
                {
                    Patterns = ["*.md"]
                },
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".txt";
            path += extension;
        }

        await File.WriteAllTextAsync(path, viewModel.BuildExportContent(extension));
    }

    private async void OnClearAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HistorySectionViewModel viewModel)
            return;

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Clear all history",
            "Delete all transcription history entries? This will also remove any session audio still attached to those records.",
            "Clear all",
            "Cancel");

        if (confirmed)
            viewModel.ClearAll();
    }
}
