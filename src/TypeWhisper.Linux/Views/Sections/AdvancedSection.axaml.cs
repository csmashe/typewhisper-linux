using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class AdvancedSection : UserControl
{
    public AdvancedSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnExportDiagnosticLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedSectionViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = Loc.Instance["Dialog.ExportDiagnosticLog"],
                    SuggestedFileName = $"typewhisper-diagnostic-log-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl",
                    DefaultExtension = "jsonl",
                    FileTypeChoices = [new FilePickerFileType("JSON Lines") { Patterns = ["*.jsonl"] }],
                }
            );

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.ExportDiagnosticLogAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.DiagnosticLogStatus = Loc.Instance["Advanced.DiagnosticLogExportFailed"];
            System.Diagnostics.Trace.WriteLine($"[Diagnostics] Export picker failed: {ex.GetType().Name}");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnDeleteDiagnosticLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AdvancedSectionViewModel viewModel)
        {
            await viewModel.DeleteDiagnosticLogAsync();
        }
    }
}
