using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class UpdateBannerTests(UiSession ui)
{
    private const string Version = "9999.0.0";

    private static readonly string[] s_languages = ["en", "de", "es", "ru", "ja", "zh"];

    [Fact]
    public async Task BannerOffersReleaseNotesLaterAndSkipReadablyInEveryLanguage()
    {
        var problems = await ui.Session.Dispatch(() =>
        {
            // Its own preferences, update service and view model, so the fake release can't reach other UI tests.
            var prefs = new LinuxPreferencesService(Path.Join(UiEnvironment.Root, $"update-banner-{Guid.NewGuid():N}.json"));
            var updateCheck = new UpdateCheckService(prefs);
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(ui.Services, updateCheck);
            var runner = (HeadlessProcessRunner)ui.Services.GetRequiredService<IProcessRunner>();
            var found = new List<string>();
            try
            {
                prefs.Update(p => p with
                {
                    LastUpdateCheckUtc = DateTime.UtcNow,
                    LastKnownLatestVersion = Version,
                    LastKnownLatestUrl = "https://github.com/csmashe/typewhisper-linux/releases/tag/v" + Version,
                });
                RepublishCachedResult(updateCheck);
                Assert.True(viewModel.UpdateBannerVisible);

                foreach (var language in s_languages)
                {
                    Loc.Instance.CurrentLanguage = language;
                    foreach (var (sizeName, width, height) in MainWindowRenderTests.s_sizes)
                        found.AddRange(InspectBanner(viewModel, language, sizeName, width, height));
                }

                Loc.Instance.CurrentLanguage = "en";
                var window = new MainWindow(viewModel) { Width = 960, Height = 640 };
                window.Show();
                try
                {
                    MainWindowRenderTests.Settle();
                    runner.Reset();
                    Click(window, Loc.Instance["Update.ReleaseNotes"]);
                    Assert.Contains("LaunchUri https", runner.Requests);

                    var before = DateTime.UtcNow;
                    Click(window, Loc.Instance["Update.Later"]);
                    Assert.False(viewModel.UpdateBannerVisible);
                    Assert.InRange(prefs.Current.UpdateSnoozedUntilUtc!.Value - before,
                        TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));

                    // Once Later runs out, the next pass of the background loop brings the banner back.
                    prefs.Update(p => p with { UpdateSnoozedUntilUtc = DateTime.UtcNow.AddMinutes(-1) });
                    RepublishCachedResult(updateCheck);
                    MainWindowRenderTests.Settle();
                    Assert.True(viewModel.UpdateBannerVisible);

                    Click(window, Loc.Instance["Update.SkipVersion"]);
                    Assert.False(viewModel.UpdateBannerVisible);
                    Assert.Equal(Version, prefs.Current.DismissedUpdateVersion);
                    RepublishCachedResult(updateCheck);
                    Assert.False(viewModel.UpdateBannerVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Loc.Instance.CurrentLanguage = "en";
            }

            return found;
        }, CancellationToken.None);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static List<string> InspectBanner(MainWindowViewModel viewModel, string language, string sizeName, double width, double height)
    {
        var window = new MainWindow(viewModel) { Width = width, Height = height };
        window.Show();
        try
        {
            MainWindowRenderTests.Settle();
            window.CaptureRenderedFrame()?.Save(
                Path.Join(UiEnvironment.SnapshotDir, $"UpdateBanner-{language}-{sizeName}.png"), PngBitmapEncoderOptions.Default);

            string[] bannerText =
            [
                Loc.Instance.GetString("Update.Available", Version), Loc.Instance["Update.ReleaseNotes"],
                Loc.Instance["Update.Later"], Loc.Instance["Update.SkipVersion"],
            ];
            var problems = bannerText
                .Where(text => FindButton(window, text) is not { IsEffectivelyVisible: true })
                .Select(text => $"{language}/{sizeName}: '{text}' is not shown")
                .ToList();
            // The inspector doesn't detect overlap, so check the banner ends before Open Wizard.
            var skip = FindButton(window, Loc.Instance["Update.SkipVersion"]);
            var wizard = FindButton(window, Loc.Instance["Common.OpenWizard"]);
            if (skip is not null && wizard is not null
                && skip.TranslatePoint(new Point(skip.Bounds.Width, 0), window)!.Value.X
                > wizard.TranslatePoint(new Point(0, 0), window)!.Value.X)
                problems.Add($"{language}/{sizeName}: the banner runs under Open Wizard");
            problems.AddRange(UiInspector.Inspect(window, new HashSet<string>())
                .Where(finding => bannerText.Any(text => finding.Detail.Contains(text, StringComparison.Ordinal)))
                .Select(finding => $"{language}/{sizeName}: {finding.Kind} {finding.Detail} at {finding.Location}"));
            return problems;
        }
        finally
        {
            window.Close();
        }
    }

    // A background-loop pass with a check that isn't due yet: it republishes the cached release,
    // then waits for the next one, which the cancelled token ends.
    private static void RepublishCachedResult(UpdateCheckService updateCheck)
    {
        using var cts = new CancellationTokenSource();
        var loop = updateCheck.RunBackgroundChecksAsync(() => false, cts.Token);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => loop.GetAwaiter().GetResult());
    }

    private static Button? FindButton(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            Equals(button.Content, text)
            || button.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text));

    private static void Click(Window window, string text)
    {
        var button = FindButton(window, text);
        Assert.NotNull(button);
        Assert.True(button.Command?.CanExecute(button.CommandParameter) ?? false, text);
        button.Command!.Execute(button.CommandParameter);
        MainWindowRenderTests.Settle();
    }
}
