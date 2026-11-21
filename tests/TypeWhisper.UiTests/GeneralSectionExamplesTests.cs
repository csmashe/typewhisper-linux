using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class GeneralSectionExamplesTests(UiSession ui)
{
    [Fact]
    public async Task CliExampleCopyButtonCopiesItsCommandUnderItsCaption()
    {
        await ui.Session.Dispatch(async () =>
        {
            var vm = ui.Services.GetRequiredService<GeneralSectionViewModel>();
            var window = new Window { Width = 960, Height = 2600, Content = new GeneralSection { DataContext = vm } };
            window.Show();
            try
            {
                // The CLI card only shows while the API is enabled; reveal it without starting the server.
                var cliList = window.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(control => ReferenceEquals(control.ItemsSource, vm.CliExamples));
                foreach (var ancestor in cliList.GetVisualAncestors())
                    ancestor.IsVisible = true;
                MainWindowRenderTests.Settle();

                var jsonExample = vm.CliExamples.Single(example => example.Command.EndsWith("--json", StringComparison.Ordinal));
                Assert.Equal(Loc.Instance["General.CliExampleJson"], jsonExample.Label);
                var captions = cliList.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block is not SelectableTextBlock && block.IsEffectivelyVisible)
                    .Select(block => block.Text)
                    .ToList();
                Assert.Contains(jsonExample.Label, captions);

                var copy = cliList.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.DataContext, jsonExample));
                Assert.Equal(
                    Loc.Instance.GetString("General.CopyCommandNamed", jsonExample.Label),
                    Avalonia.Automation.AutomationProperties.GetName(copy));
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MainWindowRenderTests.Settle();

                var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
                Assert.Equal(jsonExample.Command, await clipboard.TryGetTextAsync());
                MainWindowRenderTests.Settle();
                Assert.Equal(Loc.Instance["General.CommandCopied"], vm.CliCopyStatusText);
                Assert.Equal("", vm.CurlCopyStatusText);

                window.CaptureRenderedFrame()?.Save(
                    Path.Join(UiEnvironment.SnapshotDir, "General-cli-examples-960.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CurlExamplesGetACopyButtonButNoCaption()
    {
        await ui.Session.Dispatch(async () =>
        {
            var vm = ui.Services.GetRequiredService<GeneralSectionViewModel>();
            Assert.NotEmpty(vm.CurlExamples);
            Assert.All(vm.CurlExamples, example =>
            {
                Assert.False(example.HasLabel);
                Assert.Equal(Loc.Instance["General.CopyCommand"], example.CopyName);
            });
            Assert.All(vm.CliExamples, example => Assert.True(example.HasLabel));

            var window = new Window { Width = 960, Height = 1400, Content = new GeneralSection { DataContext = vm } };
            window.Show();
            try
            {
                MainWindowRenderTests.Settle();
                var curlList = window.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(control => ReferenceEquals(control.ItemsSource, vm.CurlExamples));
                var statusExample = vm.CurlExamples.Single(example => example.Command.EndsWith("/v1/status", StringComparison.Ordinal));
                var copy = curlList.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.DataContext, statusExample));
                Assert.True(copy.IsEffectivelyVisible);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MainWindowRenderTests.Settle();

                Assert.Equal(statusExample.Command, await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync());
                MainWindowRenderTests.Settle();
                Assert.Equal(Loc.Instance["General.CommandCopied"], vm.CurlCopyStatusText);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BrowserExtensionButtonsHandTheStorePagesToTheDesktop()
    {
        await ui.Session.Dispatch(() =>
        {
            var vm = ui.Services.GetRequiredService<GeneralSectionViewModel>();
            var runner = (HeadlessProcessRunner)ui.Services.GetRequiredService<IProcessRunner>();
            var window = new Window { Width = 960, Height = 1400, Content = new GeneralSection { DataContext = vm } };
            window.Show();
            try
            {
                MainWindowRenderTests.Settle();
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                var chrome = buttons.Single(button => Equals(button.Content, Loc.Instance["General.BrowserExtensionChrome"]));
                var firefox = buttons.Single(button => Equals(button.Content, Loc.Instance["General.BrowserExtensionFirefox"]));
                Assert.True(chrome.IsEffectivelyVisible);
                Assert.True(firefox.IsEffectivelyVisible);

                var before = HttpsLaunches();
                chrome.Command!.Execute(chrome.CommandParameter);
                firefox.Command!.Execute(firefox.CommandParameter);
                Assert.Equal(before + 2, HttpsLaunches());

                // The headless boundary refuses every launch, so the row reports the failure.
                Assert.Equal(Loc.Instance["General.BrowserExtensionOpenFailed"], vm.BrowserExtensionStatusText);
            }
            finally
            {
                window.Close();
            }

            Assert.Equal(
                "https://chromewebstore.google.com/detail/typewhisper/gamnkdngnfamiajnbeglodoaoaifbheh",
                UrlLauncher.NormalizeHttpUrl(GeneralSectionViewModel.ChromeExtensionUrl));
            Assert.Equal(
                "https://addons.mozilla.org/firefox/addon/typewhisper/",
                UrlLauncher.NormalizeHttpUrl(GeneralSectionViewModel.FirefoxExtensionUrl));
            return true;

            int HttpsLaunches() => runner.Requests.Count(request => request == "LaunchUri https");
        }, CancellationToken.None);
    }
}
