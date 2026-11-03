using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ProfilesSmallWindowTests(UiSession ui)
{
    [Theory]
    [MemberData(nameof(MainWindowRenderTests.Languages), MemberType = typeof(MainWindowRenderTests))]
    public async Task SetupButtonsFitTheProfileEditorInASmallWindow(string language)
    {
        var overflow = await ui.Session.Dispatch(() =>
        {
            Loc.Instance.CurrentLanguage = language;
            // The Window Calls install button needs the detection banner on GNOME.
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");
            var failures = ui.Services.GetRequiredService<IDetectionFailureTracker>();
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var profiles = (ProfilesSectionViewModel)viewModel.NavItems.First(item => item.Content is ProfilesSectionViewModel).Content!;
            var statusMessage = profiles.BrowserAccessibilityStatusMessage;
            var canEnable = profiles.CanEnableBrowserAccessibility;
            var canRevert = profiles.CanRevertBrowserAccessibility;
            var window = new MainWindow(viewModel) { Width = 800, Height = 600 };
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.First(item => item.Content == profiles);
                if (!profiles.HasSelectedProfile)
                    profiles.AddProfileCommand.Execute(null);
                for (var i = 0; i < 10; i++)
                    failures.RecordFailure("gnome-shell", "Window detection is unavailable");
                MainWindowRenderTests.Settle();
                profiles.BrowserAccessibilityStatusMessage = Loc.Instance["Profiles.BrowserStatusPartial"];
                profiles.CanEnableBrowserAccessibility = true;
                profiles.CanRevertBrowserAccessibility = true;
                MainWindowRenderTests.Settle();

                string[] keys = ["Profiles.InstallWindowCallsExtension", "Profiles.EnableBrowserUrlDetection", "Profiles.RevertBrowserUrlDetection"];
                var labels = keys.Select(key => Loc.Instance[key]).ToHashSet(StringComparer.Ordinal);
                var buttons = window.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible && Label(button) is { } label && labels.Contains(label))
                    .ToList();
                Assert.True(labels.Count == buttons.Count, "found: " + string.Join(" | ", buttons.Select(Label)));

                buttons[^1].BringIntoView();
                MainWindowRenderTests.Settle();
                window.CaptureRenderedFrame()?.Save(
                    Path.Join(UiEnvironment.SnapshotDir, $"Profiles-setup-{language}-800x600.png"), PngBitmapEncoderOptions.Default);

                // A button can be cut off by the scroll viewport, or its text inside the button.
                var cutOff = buttons
                    .Select(button =>
                    {
                        var viewer = button.FindAncestorOfType<ScrollViewer>()!;
                        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), viewer)!.Value.X;
                        return (Label: Label(button), Overflow: right - viewer.Viewport.Width);
                    })
                    .Where(item => item.Overflow > 0.5)
                    .Select(item => $"'{item.Label}' extends {item.Overflow:0.#}px past the viewport");
                var clipped = buttons
                    .SelectMany(button => UiInspector.Inspect(button, new HashSet<string>()))
                    .Where(finding => finding.Kind == UiInspector.ClippedText)
                    .Select(finding => $"{finding.Kind}: {finding.Detail}");
                var unnamed = buttons
                    .Where(button => ControlAutomationPeer.CreatePeerForElement(button).GetName() != Label(button))
                    .Select(button => $"'{Label(button)}' has no matching accessible name");
                return cutOff.Concat(clipped).Concat(unnamed).ToList();
            }
            finally
            {
                window.Close();
                failures.RecordSuccess();
                profiles.BrowserAccessibilityStatusMessage = statusMessage;
                profiles.CanEnableBrowserAccessibility = canEnable;
                profiles.CanRevertBrowserAccessibility = canRevert;
                Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
                Loc.Instance.CurrentLanguage = "en";
            }
        }, CancellationToken.None);

        Assert.True(overflow.Count == 0, string.Join("\n", overflow));
    }

    private static string? Label(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text,
        _ => null,
    };
}
