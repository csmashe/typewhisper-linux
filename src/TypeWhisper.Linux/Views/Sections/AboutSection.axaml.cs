using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class AboutSection : UserControl
{
    public AboutSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnExportDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.ExportDiagnostics"],
                    SuggestedFileName = "typewhisper-diagnostics.json",
                    DefaultExtension = "json",
                    FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
                }
            );

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await File.WriteAllTextAsync(path, viewModel.ExportDiagnostics());
            }
        }
        catch (Exception ex)
        {
            await ShowMessage("Export diagnostics failed", ex.Message);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnBackupSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.BackUpSettings"],
                    SuggestedFileName =
                        $"typewhisper-settings-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                    DefaultExtension = "zip",
                    FileTypeChoices =
                        [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }]
                }
            );

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var result = await viewModel.CreateSettingsBackupAsync(path);
            await ShowMessage(
                "Settings backup",
                $"Backup created with {result.FileCount} file(s)."
            );
        }
        catch (Exception ex)
        {
            await ShowMessage("Settings backup failed", ex.Message);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnRestoreSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutSectionViewModel viewModel)
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
                    Title = Loc.Instance["Dialog.RestoreSettings"],
                    AllowMultiple = false,
                    FileTypeFilter =
                        [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }]
                }
            );

            var path = (files.Count > 0 ? files[0] : null)?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var result = await viewModel.RestoreSettingsBackupAsync(path);
            await ShowMessage(
                "Settings restored",
                $"Restored {result.FileCount} file(s). Some restored settings may require an app restart."
            );
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