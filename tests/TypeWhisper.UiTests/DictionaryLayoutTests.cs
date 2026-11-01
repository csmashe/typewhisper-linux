using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class DictionaryLayoutTests(UiSession ui)
{
    // 1280x800 GNOME display minus the top bar and title bar, as measured in QA.
    [Theory]
    [InlineData(1280, 731)]
    [InlineData(960, 640)]
    public async Task AddControlsCanBeScrolledIntoView(double width, double height)
    {
        await ui.Session.Dispatch(() =>
        {
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(viewModel) { Width = width, Height = height };
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.First(item => item.Content is DictionarySectionViewModel);
                MainWindowRenderTests.Settle();

                var controls = window.GetVisualDescendants().OfType<Control>().ToList();
                var original = controls.OfType<TextBox>()
                    .Single(box => AutomationProperties.GetName(box) == Loc.Instance["History.Original"]);
                var add = controls.OfType<Button>()
                    .Single(button => Equals(button.Content, Loc.Instance["Dictionary.Add"]));

                foreach (var control in new Control[] { original, add })
                {
                    control.BringIntoView();
                    MainWindowRenderTests.Settle();
                    var bounds = control.GetTransformedBounds()!.Value;
                    var visible = bounds.Bounds.TransformToAABB(bounds.Transform).Intersect(bounds.Clip);
                    Assert.True(visible.Height >= control.Bounds.Height - 0.5,
                        $"{control.GetType().Name} is not reachable at {width}x{height}: visible {visible}, clip {bounds.Clip}");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
