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

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, viewModel.ExportToCsv());
    }

    private async void OnImport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DictionarySectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

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

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var imported = viewModel.ImportFromCsv(await File.ReadAllTextAsync(path));
        var dialog = new MessageDialogWindow();
        await dialog.ShowMessageAsync("Import dictionary", $"Imported {imported} dictionary entr{(imported == 1 ? "y" : "ies")}.");
    }
}
