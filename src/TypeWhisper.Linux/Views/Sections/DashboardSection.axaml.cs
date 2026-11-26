using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DashboardSection : UserControl
{
    private static readonly TimeSpan s_copyStatusDuration = TimeSpan.FromSeconds(3);
    private DispatcherTimer? _copyStatusTimer;

    public DashboardSection()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => _copyStatusTimer?.Stop();
    }

    // DashboardSectionViewModel holds no shell reference, so reach the shell through the window.
    private void OnOpenRecentInHistory(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Control { DataContext: DashboardRecentActivityRow row }
            && TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel shell
        )
        {
            shell.OpenHistoryRecordCommand.Execute(row.Record.Id);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; the void return is required by the RoutedEventHandler delegate signature
    private async void OnCopyRecent(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DashboardRecentActivityRow { HasText: true } row })
        {
            return;
        }

        string status;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                throw new InvalidOperationException("No clipboard is available.");
            }

            await clipboard.SetTextAsync(row.Record.FinalText);
            status = Loc.Instance["RecentTranscriptions.CopiedToClipboard"];
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DashboardSection] Copy to clipboard failed: {ex.Message}");
            status = Loc.Instance["Dashboard.CopyFailed"];
        }

        ShowCopyStatus(status);
    }

    private void ShowCopyStatus(string status)
    {
        RecentCopyStatus.Text = status;
        if (_copyStatusTimer is null)
        {
            _copyStatusTimer = new DispatcherTimer { Interval = s_copyStatusDuration };
            _copyStatusTimer.Tick += (_, _) =>
            {
                _copyStatusTimer.Stop();
                RecentCopyStatus.Text = "";
            };
        }

        _copyStatusTimer.Stop();
        _copyStatusTimer.Start();
    }
}
