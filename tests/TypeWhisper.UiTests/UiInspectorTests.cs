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

    [Theory]
    [InlineData("AccelerationOption { Value = auto, DisplayName = Auto }", true)]
    [InlineData("TypeWhisper.Linux.ViewModels.Sections.InterfaceThemeOption", true)]
    [InlineData("Avalonia.Controls.PathIcon", true)]
    [InlineData("ToggleSwitch", true)]
    [InlineData("⌄", true)]
    [InlineData(".*", true)]
    [InlineData("Auto", false)]
    [InlineData("Enabled: Script Runner", false)]
    [InlineData("音声入力", false)]
    public void IsCodeLike_FlagsTypeNamesRecordDumpsAndBareGlyphs(string text, bool expected)
    {
        Assert.Equal(expected, UiInspector.IsCodeLike(text));
    }

    [Fact]
    public async Task ComboBoxValue_ReadAsARecordDump_IsReported()
    {
        await ui.Session.Dispatch(() =>
        {
            var combo = new ComboBox
            {
                ItemsSource = new[] { new SampleOption("Automatic") },
                SelectedIndex = 0,
            };
            Avalonia.Automation.AutomationProperties.SetName(combo, "Mode");
            var window = new Window { Width = 300, Height = 100, Content = combo };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Contains(UiInspector.Inspect(window, new HashSet<string>()),
                    finding => finding.Kind == UiInspector.CodeLikeName && finding.Detail.Contains("SampleOption {", StringComparison.Ordinal));

                combo.ItemsSource = new[] { new LabeledOption("Automatic") };
                combo.SelectedIndex = 0;
                window.UpdateLayout();
                Assert.DoesNotContain(UiInspector.Inspect(window, new HashSet<string>()),
                    finding => finding.Kind == UiInspector.CodeLikeName);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local -- read only through the generated ToString() the test checks.
    private sealed record SampleOption(string Label);

    private sealed record LabeledOption(string Label)
    {
        public override string ToString() => Label;
    }
}
