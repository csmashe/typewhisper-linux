using Avalonia.Controls;
using Avalonia.Threading;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class PromptPaletteWarningTests(UiSession ui)
{
    [Fact]
    public async Task Prompt_failure_warning_stays_above_the_focused_app()
    {
        await ui.Session.Dispatch(() =>
        {
            var opened = new List<Window>();
            using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => opened.Add(window));

            _ = PromptPaletteService.ShowWarningAsync("TypeWhisper", "Prompt processing failed: 402");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!opened.OfType<MessageDialogWindow>().Any() && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
            }

            var dialog = Assert.Single(opened.OfType<MessageDialogWindow>());
            try
            {
                Assert.True(dialog.Topmost);
            }
            finally
            {
                dialog.Close();
            }
        }, CancellationToken.None);
    }
}
