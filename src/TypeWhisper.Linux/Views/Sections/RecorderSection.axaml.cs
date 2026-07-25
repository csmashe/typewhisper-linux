using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class RecorderSection : UserControl
{
    public RecorderSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnCopyTranscript(object? sender, RoutedEventArgs e)
    {
        if (
            sender is not Button { Tag: string transcript }
            || string.IsNullOrWhiteSpace(transcript)
        )
        {
            return;
        }

        await UiOperations.RunAsync(
            "copy recorder transcript",
            Loc.Instance["Common.Copy"],
            UiFailureKind.Clipboard,
            async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(transcript);
                }
            },
            presenter: message =>
            {
                if (DataContext is RecorderSectionViewModel viewModel)
                {
                    viewModel.StatusText = message;
                }

                return Task.CompletedTask;
            }
        );
    }

    private static UiOperationGuard UiOperations =>
        Program.Services.GetRequiredService<UiOperationGuard>();
}
