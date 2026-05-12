using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Views.Sections;

public partial class AboutSection : UserControl
{
    public AboutSection() => InitializeComponent();

    private async void OnExportDiagnostics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export diagnostics",
            SuggestedFileName = "typewhisper-diagnostics.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, viewModel.ExportDiagnostics());
    }

    private async void OnBackupSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up TypeWhisper settings",
            SuggestedFileName = $"typewhisper-settings-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("Zip archive")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var result = await viewModel.CreateSettingsBackupAsync(path);
            await ShowMessage("Settings backup", $"Backup created with {result.FileCount} file(s).");
        }
        catch (Exception ex)
        {
            await ShowMessage("Settings backup failed", ex.Message);
        }
    }

    private async void OnRestoreSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore TypeWhisper settings",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zip archive")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var result = await viewModel.RestoreSettingsBackupAsync(path);
            await ShowMessage(
                "Settings restored",
                $"Restored {result.FileCount} file(s). Some restored settings may require an app restart.");
        }
        catch (Exception ex)
        {
            await ShowMessage("Settings restore failed", ex.Message);
        }
    }

    private static async Task ShowMessage(string title, string message)
    {
        var dialog = new MessageDialogWindow();
        await dialog.ShowMessageAsync(title, message);
    }
}
