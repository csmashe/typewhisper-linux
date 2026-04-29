using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DashboardSectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly HistoryInsightsService _insights;

    public enum TimeRange { Weekly, Month, AllTime }

    [ObservableProperty] private TimeRange _selectedRange;
    [ObservableProperty] private int _wordCount;
    [ObservableProperty] private int _averageWpm;
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _timeSavedLabel = "0m";
    [ObservableProperty] private string _averageWordsPerDictationLabel = "0";
    [ObservableProperty] private string _averageDurationLabel = "0s";

    public ObservableCollection<TranscriptionRecord> RecentActivity { get; } = [];
    public ObservableCollection<AppUsageInsightRow> TopApps { get; } = [];
    public bool HasTopApps => TopApps.Count > 0;

    public DashboardSectionViewModel(
        IHistoryService history,
        ISettingsService settings,
        HistoryInsightsService insights)
    {
        _history = history;
        _settings = settings;
        _insights = insights;
        _selectedRange = ReadSelectedRange(settings.Current.DashboardSelectedPeriod);
        _history.RecordsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _ = InitializeAsync();
    }

    partial void OnSelectedRangeChanged(TimeRange value)
    {
        PersistSelectedRange(value);
        Refresh();
    }

    [RelayCommand] private void ShowWeekly() => SelectedRange = TimeRange.Weekly;
    [RelayCommand] private void ShowMonth() => SelectedRange = TimeRange.Month;
    [RelayCommand] private void ShowAllTime() => SelectedRange = TimeRange.AllTime;

    private async Task InitializeAsync()
    {
        await _history.EnsureLoadedAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
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
        var insights = _insights.Build(records);
        AverageWordsPerDictationLabel = insights.AverageWordsPerDictation.ToString("0.#");
        AverageDurationLabel = FormatDuration(insights.AverageDurationSeconds);

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

        TopApps.Clear();
        foreach (var app in insights.TopApps)
            TopApps.Add(new AppUsageInsightRow(app));

        OnPropertyChanged(nameof(HasTopApps));
    }

    private void PersistSelectedRange(TimeRange value)
    {
        var current = _settings.Current;
        var encoded = (int)value;
        if (current.DashboardSelectedPeriod == encoded)
            return;

        _settings.Save(current with { DashboardSelectedPeriod = encoded });
    }

    private static TimeRange ReadSelectedRange(int value) => Enum.IsDefined(typeof(TimeRange), value)
        ? (TimeRange)value
        : TimeRange.Weekly;

    private static string FormatDuration(double seconds) =>
        seconds < 60
            ? $"{seconds:0.#}s"
            : $"{seconds / 60.0:0.#}m";
}

public sealed class AppUsageInsightRow
{
    public string AppProcessName { get; }
    public int RecordCount { get; }
    public int WordCount { get; }
    public string Summary => $"{RecordCount} dictations · {WordCount} words";

    public AppUsageInsightRow(AppUsageInsight insight)
    {
        AppProcessName = insight.AppProcessName;
        RecordCount = insight.RecordCount;
        WordCount = insight.WordCount;
    }
}
