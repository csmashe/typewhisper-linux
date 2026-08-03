using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class FileTranscriptionSection : UserControl
{
    public FileTranscriptionSection()
    {
        InitializeComponent();
        // Avalonia requires drag-and-drop to be wired up in code-behind;
        // there is no XAML attribute equivalent for DragDrop event handlers.
        DragDrop.SetAllowDrop(DropZone, true);
        DragDrop.AddDragEnterHandler(DropZone, OnDragEnter);
        DragDrop.AddDragLeaveHandler(DropZone, OnDragLeave);
        DragDrop.AddDragOverHandler(DropZone, OnDragOver);
        DragDrop.AddDropHandler(DropZone, OnDrop);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnSelectFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = Loc.Instance["Dialog.SelectFiles"], AllowMultiple = true }
        );

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        if (paths.Length > 0)
        {
            viewModel.AddFilesCommand.Execute(paths);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null && !string.IsNullOrWhiteSpace(viewModel.ResultText))
        {
            await topLevel.Clipboard.SetTextAsync(viewModel.ResultText);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnCopyItem(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FileTranscriptionQueueItemViewModel item)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null && !string.IsNullOrWhiteSpace(item.ResultText))
        {
            await topLevel.Clipboard.SetTextAsync(item.ResultText);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnExportText(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || string.IsNullOrWhiteSpace(viewModel.ResultText))
        {
            return;
        }

        await ExportTextAsync(viewModel, viewModel.SelectedItem);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnExportItemText(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is not FileTranscriptionSectionViewModel viewModel
            || (sender as Control)?.DataContext is not FileTranscriptionQueueItemViewModel item
        )
        {
            return;
        }

        await ExportTextAsync(viewModel, item);
    }

    private async Task ExportTextAsync(
        FileTranscriptionSectionViewModel viewModel,
        FileTranscriptionQueueItemViewModel? item
    )
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var content = item is null ? viewModel.BuildExportText() : FileTranscriptionSectionViewModel.BuildExportText(item);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var baseName = viewModel.GetExportBaseName(item) ?? "transcription";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = Loc.Instance["Dialog.ExportText"],
                SuggestedFileName = $"{baseName}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            }
        );

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, content);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnExportItemSrt(object? sender, RoutedEventArgs e)
    {
        await ExportSubtitleAsync(sender, "srt", "SRT");
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnExportItemVtt(object? sender, RoutedEventArgs e)
    {
        await ExportSubtitleAsync(sender, "vtt", "WebVTT");
    }

    private async Task ExportSubtitleAsync(object? sender, string extension, string label)
    {
        if (
            DataContext is not FileTranscriptionSectionViewModel viewModel
            || (sender as Control)?.DataContext is not FileTranscriptionQueueItemViewModel item
        )
        {
            return;
        }

        var content = FileTranscriptionSectionViewModel.BuildSubtitleExport(item, extension);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var baseName = viewModel.GetExportBaseName(item) ?? "transcription";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = $"Export {label}",
                SuggestedFileName = $"{baseName}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{extension}"] }],
            }
        );

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, content);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnSelectWatchFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
        {
            return;
        }

        var path = await PickFolderAsync("Select watch folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SetWatchFolderPath(path);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnSelectWatchFolderOutput(
        object? sender,
        RoutedEventArgs e
    )
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
        {
            return;
        }

        var path = await PickFolderAsync("Select output folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SetWatchFolderOutputPath(path);
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false }
        );

        return (folders.Count > 0 ? folders[0] : null)?.TryGetLocalPath();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);

        if (DataContext is not FileTranscriptionSectionViewModel { CanImportFiles: true } viewModel)
        {
            return;
        }

        var items = e.DataTransfer.TryGetFiles();
        var paths = items
            ?.Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths is { Length: > 0 })
        {
            viewModel.HandleFileDrop(paths);
        }
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var canAccept =
            DataContext is FileTranscriptionSectionViewModel { CanImportFiles: true }
            && e.DataTransfer.Contains(DataFormat.File);

        e.DragEffects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(canAccept);
    }

    private void SetDragOver(bool isDragOver)
    {
        DropZone.Classes.Set("drag-over", isDragOver);
        if (DataContext is FileTranscriptionSectionViewModel viewModel)
        {
            viewModel.IsDragOver = isDragOver;
        }
    }
}