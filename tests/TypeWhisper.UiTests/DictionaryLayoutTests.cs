using Avalonia;
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

    [Theory]
    [MemberData(nameof(MainWindowRenderTests.Languages), MemberType = typeof(MainWindowRenderTests))]
    public async Task EntryRowsKeepEveryPartApartAndInsideTheRow(string language)
    {
        var problems = await ui.Session.Dispatch(() =>
        {
            // Long name and every badge, so these rows are about as wide as a row gets.
            DictionaryEntry[] entries =
            [
                new()
                {
                    Id = "row-fit-term", Original = "TWQA_München_日本語_Kubernetes", EntryType = DictionaryEntryType.Term,
                    CaseSensitive = true, UsageCount = 1234, Priority = 10,
                },
                new()
                {
                    Id = "row-fit-correction", Original = "TWQA_teh_ä", Replacement = "TWQA_the_ü",
                    EntryType = DictionaryEntryType.Correction, CaseSensitive = true, IsRegex = true, UsageCount = 1234, Priority = 10,
                },
                // Long enough that an uncapped name would push the actions out of a wide row.
                new()
                {
                    Id = "row-fit-long",
                    Original = "TWQA a very long dictionary phrase that keeps going well past the width of a normal entry name",
                    Replacement = "TWQA_an_equally_long_replacement_without_any_spaces_to_break_the_line_at_all_ü",
                    EntryType = DictionaryEntryType.Correction, UsageCount = 1234, Priority = 10,
                },
            ];
            var dictionary = ui.Services.GetRequiredService<IDictionaryService>();
            foreach (var entry in entries)
                dictionary.AddEntry(entry);
            Loc.Instance.CurrentLanguage = language;
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(viewModel) { Width = 800, Height = 600 };
            window.Show();
            var found = new List<string>();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.First(item => item.Content is DictionarySectionViewModel);
                // 800x600 is the smallest size QA got a window manager to accept.
                for (var width = 800; width <= 1400; width += 20)
                {
                    window.Width = width;
                    MainWindowRenderTests.Settle();
                    var cards = window.GetVisualDescendants().OfType<Border>()
                        .Where(border => border.Classes.Contains("entry-card")
                            && border.DataContext is DictionaryEntry { Id: "row-fit-term" or "row-fit-correction" or "row-fit-long" })
                        .ToList();
                    if (cards.Count != entries.Length)
                    {
                        found.Add($"{width}px: found {cards.Count} of {entries.Length} rows");
                        continue;
                    }

                    foreach (var card in cards)
                        found.AddRange(RowProblems(card).Select(problem =>
                            $"{width}px {((DictionaryEntry)card.DataContext!).Id}: {problem}"));
                }
            }
            finally
            {
                window.Close();
                Loc.Instance.CurrentLanguage = "en";
                foreach (var entry in entries)
                    dictionary.DeleteEntry(entry.Id);
            }

            return found;
        }, CancellationToken.None);

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    private static IEnumerable<string> RowProblems(Border card)
    {
        var grid = (Grid)card.Child!;
        var name = grid.Children.OfType<StackPanel>().Single(panel => panel.Classes.Contains("entry-name"));
        var badges = grid.Children.OfType<WrapPanel>().Single(panel => panel.Classes.Contains("entry-badges"));
        var actions = grid.Children.OfType<WrapPanel>().Single(panel => panel.Classes.Contains("entry-actions"));
        var parts = new List<(string Label, Rect Bounds)> { ("term", BoundsIn(name, card)) };
        parts.AddRange(badges.Children.Where(badge => badge.IsVisible)
            .Select(badge => ($"badge '{((TextBlock)((Border)badge).Child!).Text}'", BoundsIn(badge, card))));
        parts.AddRange(actions.GetVisualDescendants().OfType<Control>()
            .Where(control => control is Button or ToggleSwitch && control.IsEffectivelyVisible)
            .Select(control => ($"{control.GetType().Name} '{(control as ContentControl)?.Content}'", BoundsIn(control, card))));

        var inside = card.Bounds.Width - card.Padding.Right + 0.5;
        foreach (var (label, bounds) in parts.Where(part => part.Bounds.Right > inside))
            yield return $"{label} ends at {bounds.Right:0.#}, past the row's {inside - 0.5:0.#}";
        for (var i = 0; i < parts.Count; i++)
        for (var j = i + 1; j < parts.Count; j++)
        {
            var overlap = parts[i].Bounds.Intersect(parts[j].Bounds);
            if (overlap.Width > 0.5 && overlap.Height > 0.5)
                yield return $"{parts[i].Label} overlaps {parts[j].Label}";
        }
    }

    private static Rect BoundsIn(Visual visual, Visual ancestor) =>
        new(visual.TranslatePoint(default, ancestor)!.Value, visual.Bounds.Size);

    private static void AssertFullyVisible(Control control, double width, double height)
    {
        var bounds = control.GetTransformedBounds()!.Value;
        var visible = bounds.Bounds.TransformToAABB(bounds.Transform).Intersect(bounds.Clip);
        Assert.True(visible.Height >= control.Bounds.Height - 0.5,
            $"{control.GetType().Name} is not reachable at {width}x{height}: visible {visible}, clip {bounds.Clip}");
    }
}
