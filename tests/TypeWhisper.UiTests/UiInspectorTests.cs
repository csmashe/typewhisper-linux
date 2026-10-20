using Avalonia;
using Avalonia.Controls;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class UiInspectorTests(UiSession ui)
{
    [Theory]
    [InlineData(-50)]
    [InlineData(50)]
    public async Task TextThatFitsItsOwnBoxButCrossesAncestorClip_IsReported(double offset)
    {
        await ui.Session.Dispatch(() =>
        {
            var text = new TextBlock { Text = "A long visible label", Width = 150, FontSize = 14 };
            Canvas.SetLeft(text, offset);
            var canvas = new Canvas { Width = 150, Height = 40, ClipToBounds = true };
            canvas.Children.Add(text);
            var window = new Window { Width = 300, Height = 100, Content = canvas };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(text.TextLayout.Width <= text.Bounds.Width);
                Assert.Contains(UiInspector.Inspect(window, new HashSet<string>()), f => f.Kind == UiInspector.ClippedText);
                Canvas.SetLeft(text, 0);
                window.UpdateLayout();
                Assert.DoesNotContain(UiInspector.Inspect(window, new HashSet<string>()), f => f.Kind == UiInspector.ClippedText);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
