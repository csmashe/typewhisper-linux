using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
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
                    AssertFullyVisible(control, width, height);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EntryAliasesButtonScrollsPanelIntoView()
    {
        await ui.Session.Dispatch(() =>
        {
            var term = new DictionaryEntry { Id = "alias-scroll-term", Original = "Kubernetes", EntryType = DictionaryEntryType.Term };
            var dictionary = ui.Services.GetRequiredService<IDictionaryService>();
            dictionary.AddEntry(term);
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(viewModel) { Width = 960, Height = 640 };
            DictionarySectionViewModel? section = null;
            window.Show();
            try
            {
                var nav = viewModel.NavItems.First(item => item.Content is DictionarySectionViewModel);
                section = (DictionarySectionViewModel)nav.Content!;
                viewModel.SelectedItem = nav;
                MainWindowRenderTests.Settle();

                var controls = window.GetVisualDescendants().OfType<Control>().ToList();
                var add = controls.OfType<Button>()
                    .Single(button => Equals(button.Content, Loc.Instance["Dictionary.Add"]));
                add.BringIntoView();
                MainWindowRenderTests.Settle();

                var panel = controls.OfType<Border>().Single(border => border.Name == "AliasPanel");
                var aliases = controls.OfType<Button>()
                    .First(button => button.IsEffectivelyVisible && Equals(button.Content, Loc.Instance["Dictionary.AliasesForEntry"]));
                ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(aliases)).Invoke();
                MainWindowRenderTests.Settle();

                Assert.True(panel.IsVisible);
                // The panel can be taller than the viewport, so its title stands in for "scrolled to it".
                AssertFullyVisible(((StackPanel)panel.Child!).Children[0], 960, 640);
            }
            finally
            {
                // The section is shared, so an open panel would leak its English status into later language checks.
                section?.AliasSuggestions.CloseCommand.Execute(null);
                window.Close();
                dictionary.DeleteEntry(term.Id);
            }
        }, CancellationToken.None);
    }

    private static void AssertFullyVisible(Control control, double width, double height)
    {
        var bounds = control.GetTransformedBounds()!.Value;
        var visible = bounds.Bounds.TransformToAABB(bounds.Transform).Intersect(bounds.Clip);
        Assert.True(visible.Height >= control.Bounds.Height - 0.5,
            $"{control.GetType().Name} is not reachable at {width}x{height}: visible {visible}, clip {bounds.Clip}");
    }
}
