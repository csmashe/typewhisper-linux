using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class AboutLongPathTests(UiSession ui)
{
    [Fact]
    public async Task LongInstallAndDataPathsStayReadableInACompactWindow()
    {
        var clipped = await ui.Session.Dispatch(() =>
        {
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var about = (AboutSectionViewModel)viewModel.NavItems.First(item => item.Content is AboutSectionViewModel).Content!;
            var window = new MainWindow(viewModel) { Width = 960, Height = 640 };
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.First(item => ReferenceEquals(item.Content, about));
                MainWindowRenderTests.Settle();

                string[] shown = [about.InstallLocation, about.DataLocation];
                var paths = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Where(block => shown.Contains(block.Text))
                    .ToList();
                Assert.Equal(2, paths.Count);

                const string longPath =
                    "/mnt/data/t3-code/scratch/compositor-qa-fdf73ac1/UiTests/tmp/a-long-owned-directory/xdg/data/TypeWhisper";
                foreach (var path in paths)
                    path.Text = longPath;
                MainWindowRenderTests.Settle();
                paths[^1].BringIntoView();
                MainWindowRenderTests.Settle();
                window.CaptureRenderedFrame()?.Save(
                    Path.Join(UiEnvironment.SnapshotDir, "About-long-path-960x640.png"), PngBitmapEncoderOptions.Default);

                return UiInspector.Inspect(window, new HashSet<string>())
                    .Where(finding => finding.Kind == UiInspector.ClippedText && finding.Detail.Contains(longPath[..40], StringComparison.Ordinal))
                    .Select(finding => $"{finding.Detail} at {finding.Location}")
                    .ToList();
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        Assert.True(clipped.Count == 0, string.Join("\n", clipped));
    }
}
