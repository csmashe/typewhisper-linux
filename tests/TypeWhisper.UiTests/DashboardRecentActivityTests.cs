using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

/// <summary>Drives a Dashboard recent-activity row in the real window: click to open it in History, and Copy.</summary>
[Collection("ui")]
public sealed class DashboardRecentActivityTests(UiSession ui)
{
    private const string RecordId = "ui-dashboard-recent";
    private const string Text = "Dashboard reveal target.";

    [Fact]
    public async Task ClickingARecentRow_OpensItExpandedInHistoryPastAHidingSearch()
    {
        var result = await ui.Session.Dispatch(() => Run(window =>
        {
            var viewModel = (MainWindowViewModel)window.DataContext!;
            var history = ui.Services.GetRequiredService<HistorySectionViewModel>();
            history.SearchQuery = "matches nothing";

            FindRecentRow(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MainWindowRenderTests.Settle();

            window.CaptureRenderedFrame()?.Save(Path.Join(UiEnvironment.SnapshotDir, "DashboardRecent-OpenedInHistory.png"), PngBitmapEncoderOptions.Default);
            Assert.IsType<HistorySectionViewModel>(viewModel.CurrentSection);
            Assert.Equal("", history.SearchQuery);
            var card = window.GetVisualDescendants().OfType<Border>().Single(border =>
                border.Classes.Contains("timeline-card") && border.DataContext is HistoryRecordRow { Record.Id: RecordId });
            Assert.True(((HistoryRecordRow)card.DataContext!).IsExpanded);
            var focused = Assert.IsType<Button>(window.FocusManager.GetFocusedElement());
            Assert.Contains("card-toggle", focused.Classes);
            Assert.Same(card.DataContext, focused.DataContext);
            return "";
        }), CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task CopyMenuItem_CopiesTheTranscriptAndAnnouncesIt()
    {
        var (copied, status) = await ui.Session.Dispatch(() => Run(window =>
        {
            var row = FindRecentRow(window);
            row.BringIntoView();
            MainWindowRenderTests.Settle();
            var menu = row.ContextMenu!;
            menu.Open(row);
            MainWindowRenderTests.Settle();
            window.CaptureRenderedFrame()?.Save(Path.Join(UiEnvironment.SnapshotDir, "DashboardRecent-ContextMenu.png"), PngBitmapEncoderOptions.Default);
            try
            {
                menu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, Loc.Instance["Common.Copy"]))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var clipboard = window.Clipboard!;
                var text = Await(clipboard.TryGetTextAsync());
                MainWindowRenderTests.Settle();
                var statusText = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Name == "RecentCopyStatus").Text;
                return (text, statusText);
            }
            finally
            {
                menu.Close();
            }
        }), CancellationToken.None);

        Assert.Equal(Text, copied);
        Assert.Equal(Loc.Instance["RecentTranscriptions.CopiedToClipboard"], status);
    }

    private T Run<T>(Func<MainWindow, T> act)
    {
        var history = ui.Services.GetRequiredService<IHistoryService>();
        history.AddRecord(new TranscriptionRecord
        {
            Id = RecordId,
            Timestamp = DateTime.UtcNow,
            RawText = "dashboard reveal target",
            FinalText = Text,
        });
        var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
        var historySection = ui.Services.GetRequiredService<HistorySectionViewModel>();
        var window = new MainWindow(viewModel) { Width = 1200, Height = 780 };
        window.Show();
        try
        {
            viewModel.Navigate<DashboardSectionViewModel>();
            MainWindowRenderTests.Settle();
            return act(window);
        }
        finally
        {
            window.Close();
            historySection.SearchQuery = "";
            history.DeleteRecord(RecordId);
            viewModel.Navigate<DashboardSectionViewModel>();
            MainWindowRenderTests.Settle();
        }
    }

    private static Button FindRecentRow(MainWindow window) => window.GetVisualDescendants().OfType<Button>().Single(button =>
        button.Classes.Contains("recent-row") && button.DataContext is DashboardRecentActivityRow { Record.Id: RecordId });

    private static T Await<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(task.IsCompleted, "Timed out after 10 s waiting for the clipboard copy.");
        return task.GetAwaiter().GetResult();
    }
}
