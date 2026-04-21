using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DashboardSectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    public enum TimeRange { Weekly, Month, AllTime }

    [ObservableProperty] private TimeRange _selectedRange = TimeRange.Weekly;
    [ObservableProperty] private int _wordCount;
    [ObservableProperty] private int _averageWpm;
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _timeSavedLabel = "0m";

    public ObservableCollection<TranscriptionRecord> RecentActivity { get; } = [];

    public DashboardSectionViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _ = _history.EnsureLoadedAsync().ContinueWith(_ => Dispatcher.UIThread.Post(Refresh));
        Refresh();
    }

    partial void OnSelectedRangeChanged(TimeRange value) => Refresh();

    [RelayCommand] private void ShowWeekly() => SelectedRange = TimeRange.Weekly;
    [RelayCommand] private void ShowMonth() => SelectedRange = TimeRange.Month;
    [RelayCommand] private void ShowAllTime() => SelectedRange = TimeRange.AllTime;

    private void Refresh()
    {
        var now = DateTime.Now;
        var cutoff = SelectedRange switch
        {
            TimeRange.Weekly => now.AddDays(-7),
            TimeRange.Month => now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        var records = _history.Records.Where(r => r.Timestamp >= cutoff).ToList();
        WordCount = records.Sum(r => r.WordCount);
        var totalSeconds = records.Sum(r => r.DurationSeconds);
        AverageWpm = totalSeconds > 0
            ? (int)(WordCount / (totalSeconds / 60.0))
            : 0;
        AppCount = records.Select(r => r.AppProcessName).Where(a => !string.IsNullOrEmpty(a)).Distinct().Count();

        // Time "saved" = words typed at 150 WPM (typist baseline) minus time spoken
        var typingSeconds = WordCount / 150.0 * 60.0;
        var saved = Math.Max(0, typingSeconds - totalSeconds);
        TimeSavedLabel = saved < 60
            ? $"{(int)saved}s"
            : saved < 3600
                ? $"{(int)(saved / 60)}m"
                : $"{saved / 3600:F1}h";

        RecentActivity.Clear();
        foreach (var r in records.OrderByDescending(r => r.Timestamp).Take(10))
            RecentActivity.Add(r);
    }
}
