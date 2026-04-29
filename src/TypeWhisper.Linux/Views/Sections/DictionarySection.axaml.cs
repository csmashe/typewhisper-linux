using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DictionarySection : UserControl
{
    public DictionarySection() => InitializeComponent();

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DictionarySectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export dictionary",
                SuggestedFileName = "typewhisper-dictionary.csv",
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType("CSV")
                    {
                        Patterns = ["*.csv"]
                    }
                ]
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(viewModel.ExportToCsv());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DictionarySection] Export failed: {ex}");
            await ShowErrorAsync("Export dictionary", $"Could not export dictionary: {ex.Message}");
        }
    }

    private async void OnImport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DictionarySectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import dictionary",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("CSV")
                    {
                        Patterns = ["*.csv"]
                    }
                ]
            });

            var file = files.FirstOrDefault();
            if (file is null)
                return;

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var imported = viewModel.ImportFromCsv(await reader.ReadToEndAsync());
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync("Import dictionary", $"Imported {imported} dictionary entr{(imported == 1 ? "y" : "ies")}.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DictionarySection] Import failed: {ex}");
            await ShowErrorAsync("Import dictionary", $"Could not import dictionary: {ex.Message}");
        }
    }

    private static async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new MessageDialogWindow();
        await dialog.ShowMessageAsync(title, message);
    }
}
