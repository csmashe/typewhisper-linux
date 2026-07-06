using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class SnippetsSection : UserControl
{
    public SnippetsSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnippetsSectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.ExportSnippets"],
                    SuggestedFileName = "typewhisper-snippets.json",
                    DefaultExtension = "json",
                    FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
                }
            );

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await File.WriteAllTextAsync(path, viewModel.ExportToJson());
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SnippetsSection] Export failed: {ex.Message}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnippetsSectionViewModel viewModel)
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
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = Loc.Instance["Dialog.ImportSnippets"],
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
                }
            );

            var path = (files.Count > 0 ? files[0] : null)?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var imported = viewModel.ImportFromJson(await File.ReadAllTextAsync(path));
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync("Import snippets", $"Imported {imported} snippet(s).");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SnippetsSection] Import failed: {ex.Message}");
        }
    }
}