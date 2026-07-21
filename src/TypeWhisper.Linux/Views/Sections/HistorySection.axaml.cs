using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class HistorySection : UserControl
{
    public HistorySection()
    {
        InitializeComponent();
    }

    private void OnHistoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        LoadMoreIfNearBottom();
    }

    // Loads further pages while the bottom of the content is within the
    // threshold of the viewport. Appending rows only grows the extent on the
    // next layout pass, so the follow-up check is posted: this keeps loading
    // when a page is too short to fill the viewport (which would otherwise
    // leave no scrollbar and fire no further ScrollChanged to resume loading).
    private void LoadMoreIfNearBottom()
    {
        if (DataContext is not HistorySectionViewModel { HasMore: true } viewModel)
        {
            return;
        }

        const double threshold = 300;
        var distanceToBottom =
            HistoryScroll.Extent.Height
            - (HistoryScroll.Offset.Y + HistoryScroll.Viewport.Height);
        if (distanceToBottom > threshold)
        {
            return;
        }

        viewModel.LoadMore();

        if (viewModel.HasMore)
        {
            Dispatcher.UIThread.Post(LoadMoreIfNearBottom, DispatcherPriority.Background);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnCopyRecord(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } || string.IsNullOrWhiteSpace(text))
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
            Trace.WriteLine($"[HistorySection] Copy to clipboard failed: {ex.Message}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HistorySectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = Loc.Instance["Dialog.ExportHistory"],
                    SuggestedFileName = $"typewhisper-history-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    DefaultExtension = "txt",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                        new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                        new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                        new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                    ],
                }
            );

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                // Some Linux file choosers omit the extension when the user types a
                // bare filename; default to .txt so the export has the right format.
                extension = ".txt";
                path += extension;
            }

            await File.WriteAllTextAsync(path, viewModel.BuildExportContent(extension));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HistorySection] Export failed: {ex.Message}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnClearAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HistorySectionViewModel viewModel)
        {
            return;
        }

        try
        {
            var dialog = new MessageDialogWindow();
            var confirmed = await dialog.ShowConfirmationAsync(
                "Clear all history",
                "Delete all transcription history entries? This will also remove any session audio still attached to those records.",
                "Clear all"
            );

            if (confirmed)
            {
                viewModel.ClearAll();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HistorySection] Clear all failed: {ex.Message}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnDeleteRecord(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is not HistorySectionViewModel viewModel
            || sender is not Button { Tag: HistoryRecordRow row }
        )
        {
            return;
        }

        try
        {
            var dialog = new MessageDialogWindow();
            var confirmed = await dialog.ShowConfirmationAsync(
                "Delete history entry",
                "Delete this history entry? Any session audio still attached to it will also be removed.",
                "Delete"
            );

            if (confirmed)
            {
                viewModel.DeleteRecordCommand.Execute(row);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HistorySection] Delete record failed: {ex.Message}");
        }
    }
}