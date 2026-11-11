using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class LearnedCorrectionToastTests(UiSession ui)
{
    [Fact]
    public async Task ClickingUndoDoesNotMoveFocusIntoTheToast()
    {
        // A focused Undo button is reported over AT-SPI as the focused element while the target app
        // keeps real focus, so correction learning would skip the next dictation.
        await ui.Session.Dispatch(() =>
        {
            var undone = 0;
            var toast = new LearnedCorrectionToastWindow();
            toast.ShowToast("Learned", true, "Undo", null, null, () => undone++);
            try
            {
                Dispatcher.UIThread.RunJobs();
                toast.UpdateLayout();
                var button = toast.UndoButton;
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), toast)!.Value;
                toast.MouseDown(point, MouseButton.Left);
                toast.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, undone);
                Assert.False(button.IsFocused);
                Assert.Null(toast.FocusManager.GetFocusedElement());
            }
            finally
            {
                toast.Close();
            }
        }, CancellationToken.None);
    }
}
