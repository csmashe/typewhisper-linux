using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class FileTranscriptionSection : UserControl
{
    public FileTranscriptionSection()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(DropZone, true);
        DragDrop.AddDragEnterHandler(DropZone, OnDragEnter);
        DragDrop.AddDragLeaveHandler(DropZone, OnDragLeave);
        DragDrop.AddDragOverHandler(DropZone, OnDragOver);
        DragDrop.AddDropHandler(DropZone, OnDrop);
    }

    private async void OnSelectFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file",
            AllowMultiple = false
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.TranscribeFileCommand.Execute(path);
    }

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null && !string.IsNullOrWhiteSpace(viewModel.ResultText))
            await topLevel.Clipboard.SetTextAsync(viewModel.ResultText);
    }

    private async void OnExportText(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FileTranscriptionSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || string.IsNullOrWhiteSpace(viewModel.ResultText))
            return;

        var baseName = viewModel.FilePath is not null
            ? Path.GetFileNameWithoutExtension(viewModel.FilePath)
            : "transcription";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export text",
            SuggestedFileName = $"{baseName}.txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text")
                {
                    Patterns = ["*.txt"]
                }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, viewModel.BuildExportText());
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

        if (DataContext is not FileTranscriptionSectionViewModel viewModel || !viewModel.CanImportFiles)
            return;

        var items = e.DataTransfer.TryGetFiles();
        var paths = items?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths is { Length: > 0 })
            viewModel.HandleFileDrop(paths);
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var canAccept = DataContext is FileTranscriptionSectionViewModel { CanImportFiles: true }
            && e.DataTransfer.Contains(DataFormat.File);

        e.DragEffects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(canAccept);
    }

    private void SetDragOver(bool isDragOver)
    {
        DropZone.Classes.Set("drag-over", isDragOver);
        if (DataContext is FileTranscriptionSectionViewModel viewModel)
            viewModel.IsDragOver = isDragOver;
    }
}
