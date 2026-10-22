using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using TypeWhisper.Linux.Services.AppImport;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DictionarySection : UserControl
{
    public DictionarySection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DictionarySectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.ExportDictionary"],
                    SuggestedFileName = "typewhisper-dictionary.csv",
                    DefaultExtension = "csv",
                    FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
                }
            );

            if (file is null)
            {
                return;
            }

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

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DictionarySectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.ImportDictionary"],
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
                }
            );

            var file = files.Count > 0 ? files[0] : null;
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var imported = viewModel.ImportFromCsv(await reader.ReadToEndAsync());
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync(
                "Import dictionary",
                $"Imported {imported} dictionary entr{(imported == 1 ? "y" : "ies")}."
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DictionarySection] Import failed: {ex}");
            await ShowErrorAsync("Import dictionary", $"Could not import dictionary: {ex.Message}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnImportFromHandy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var source = AppImportSource.Handy();
        var path = AppImportSource.HandyDefaultSettingsPath();
        if (!File.Exists(path))
        {
            var choose = await new MessageDialogWindow().ShowConfirmationAsync(
                Loc.Instance.GetString("AppImport.Title", source.AppName),
                Loc.Instance.GetString("HandyImport.NotFoundChoose", path),
                Loc.Instance["AppImport.ChooseFile"]
            );
            if (!choose)
            {
                return;
            }

            path = await PickFileAsync(owner, "HandyImport.PickerTitle", "JSON", "*.json");
        }

        await RunAppImportAsync(owner, source, path);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnImportFromWispr(object? sender, RoutedEventArgs e)
    {
        // No Linux install to discover; the user picks a copied database.
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await RunAppImportAsync(
                owner,
                AppImportSource.WisprFlow,
                await PickFileAsync(owner, "WisprImport.PickerTitle", "SQLite", "*.sqlite")
            );
        }
    }

    private async Task RunAppImportAsync(Window owner, AppImportSource source, string? path)
    {
        if (path is null || DataContext is not DictionarySectionViewModel viewModel)
        {
            return;
        }

        var title = Loc.Instance.GetString("AppImport.Title", source.AppName);
        try
        {
            using var import = viewModel.CreateAppImport(source, path);
            var result = await new AppImportWindow(import).ShowDialog<string?>(owner);
            if (result is not null)
            {
                await new MessageDialogWindow().ShowMessageAsync(title, result);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DictionarySection] {source.AppName} import failed: {ex}");
            await ShowErrorAsync(title, Loc.Instance.GetString("AppImport.ReadFailed", ex.Message));
        }
    }

    private static async Task<string?> PickFileAsync(Window owner, string titleKey, string typeName, string pattern)
    {
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = Loc.Instance[titleKey],
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }, FilePickerFileTypes.All],
                }
            );
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            // Called from async void handlers, where an escaping exception would end the app.
            Trace.WriteLine($"[DictionarySection] File picker failed: {ex}");
            return null;
        }
    }

    private static async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new MessageDialogWindow();
        await dialog.ShowMessageAsync(title, message);
    }
}