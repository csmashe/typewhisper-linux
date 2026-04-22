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

    private async void OnCheckForUpdates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new MessageDialogWindow();
        await dialog.ShowMessageAsync("Check for Updates", "Automatic updates are not configured in this Linux build yet.");
    }
}
