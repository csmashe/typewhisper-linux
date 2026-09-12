using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DashboardSectionViewModel : ObservableObject, IDisposable
{
    public enum TimeRange
    {
        Weekly,
        Month,
        AllTime,
    }

    private const double ManualTypingWordsPerMinute = 40.0;

    private readonly IHistoryService _history;
    private readonly IHistoryInsightsService _insights;
    private readonly ISettingsService _settings;
    private readonly TimeZoneInfo _timeZone;
    private readonly IUsageStatisticsService _statistics;
    private readonly PluginManager? _plugins;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Action> _postToUi;

    [ObservableProperty]
    private int _daysActive;

    [ObservableProperty]
    private int _currentStreak;

    [ObservableProperty]
    private int _longestStreak;

    [ObservableProperty]
    private int _transcriptionCount;

    [ObservableProperty]
    private string _wordsTrendLabel = string.Empty;

    [ObservableProperty]
    private string _wpmTrendLabel = string.Empty;


    [ObservableProperty]
    private int _appCount;

    [ObservableProperty]
    private string _averageDurationLabel = "0s";

    [ObservableProperty]
    private string _averageWordsPerDictationLabel = "0";

    [ObservableProperty]
    private int _averageWpm;

    [ObservableProperty]
    private string _cleanupAppliedCountLabel = "0";

    [ObservableProperty]
    private string _clipboardFallbackCountLabel = "0";

    [ObservableProperty]
    private string _dictionaryCorrectionAppliedCountLabel = "0";

    private volatile bool _disposed;
    private ITimer? _dayRefreshTimer;

    [ObservableProperty]
    private string _failedInsertionCountLabel = "0";

    [ObservableProperty]
    private string _insertedBreakdownLabel = Loc.Instance.GetString("Dashboard.InsertedBreakdown", 0, 0);

    [ObservableProperty]
    private string _insertionSuccessRateLabel = "0%";

    [ObservableProperty]
    private string _pastedCountLabel = "0";

    [ObservableProperty]
    private string _promptActionAppliedCountLabel = "0";

    [ObservableProperty]
    private TimeRange _selectedRange;

    [ObservableProperty]
    private string _snippetAppliedCountLabel = "0";

    [ObservableProperty]
    private string _timeSavedLabel = "0m";

    [ObservableProperty]
    private string _translationAppliedCountLabel = "0";

    [ObservableProperty]
    private string _typedCountLabel = "0";

    [ObservableProperty]
    private int _wordCount;

    public DashboardSectionViewModel(
        IHistoryService history,
        ISettingsService settings,
        IHistoryInsightsService insights,
        IUsageStatisticsService statistics,
        PluginManager? plugins = null
    )
        : this(history, settings, insights, statistics, TimeZoneInfo.Local, plugins) { }

    internal DashboardSectionViewModel(
        IHistoryService history,
        ISettingsService settings,
        IHistoryInsightsService insights,
        IUsageStatisticsService statistics,
        TimeZoneInfo timeZone,
        PluginManager? plugins = null,
        TimeProvider? timeProvider = null,
        Action<Action>? postToUi = null
    )
    {
        _history = history;
        _settings = settings;
        _insights = insights;
        _timeZone = timeZone;
        _statistics = statistics;
        _plugins = plugins;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _postToUi = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _statistics.StatisticsChanged += OnRecordsChanged;
        if (_plugins is not null)
        {
            _plugins.PluginStateChanged += OnPluginStateChanged;
        }
        // ReadSelectedRange guards against out-of-range ints stored by older
        // versions of the app (DashboardSelectedPeriod is an unvalidated int).
        _selectedRange = ReadSelectedRange(settings.Current.DashboardSelectedPeriod);
        _history.RecordsChanged += OnRecordsChanged;
        // The localized labels (InsertedBreakdownLabel) and TopApps rows (Summary)
        // are resolved into stored strings, so rebuild them on a live language switch.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _ = InitializeAsync();
    }

    public ObservableCollection<DashboardRecentActivityRow> RecentActivity { get; } = [];
    public ObservableCollection<AppUsageInsightRow> TopApps { get; } = [];
    public ObservableCollection<DashboardActivityDayRow> ActivityDays { get; } = [];
    public ObservableCollection<DashboardBreakdownRow> Languages { get; } = [];
    public ObservableCollection<DashboardBreakdownRow> Models { get; } = [];
    public ObservableCollection<DashboardHourRow> Hours { get; } = [];
    public bool HasActivity => TranscriptionCount > 0;
    public bool HasLanguages => Languages.Count > 0;
    public bool HasModels => Models.Count > 0;
    public string CurrentStreakLabel => Loc.Instance.GetString("Dashboard.DaysFormat", CurrentStreak);
    public string LongestStreakLabel => Loc.Instance.GetString("Dashboard.DaysFormat", LongestStreak);
    public bool HasTopApps => TopApps.Count > 0;
    public bool HasRecentActivity => RecentActivity.Count > 0;

    public void Dispose()
    {
        _disposed = true;
        _dayRefreshTimer?.Dispose();
        _history.RecordsChanged -= OnRecordsChanged;
        _statistics.StatisticsChanged -= OnRecordsChanged;
        if (_plugins is not null)
        {
            _plugins.PluginStateChanged -= OnPluginStateChanged;
        }
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedRangeChanged(TimeRange value)
    {
        PersistSelectedRange(value);
        Refresh();
    }

    [RelayCommand]
    private void ShowWeekly()
    {
        SelectedRange = TimeRange.Weekly;
    }

    [RelayCommand]
    private void ShowMonth()
    {
        SelectedRange = TimeRange.Month;
    }

    [RelayCommand]
    private void ShowAllTime()
    {
        SelectedRange = TimeRange.AllTime;
    }

    private async Task InitializeAsync()
    {
        await _history.EnsureLoadedAsync().ConfigureAwait(false);
        if (!_disposed)
        {
            _postToUi(Refresh);
        }
    }

    private void OnRecordsChanged()
    {
        if (!_disposed)
        {
            _postToUi(Refresh);
        }
    }

    // LanguageChanged is raised on the UI thread (from the settings binding), so
    // Refresh can run directly and re-resolve the localized labels/rows in place.
    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void OnPluginStateChanged(object? sender, EventArgs e) => OnRecordsChanged();

    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = SelectedRange switch
        {
            TimeRange.Weekly => now.AddDays(-7),
            TimeRange.Month => now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        var records = _history.Records.Where(r => r.Timestamp >= cutoff && r.Status == TranscriptionRecordStatus.Succeeded).ToList();
        RefreshStatistics(DateOnly.FromDateTime(PresentationDateTime.ToLocal(now, _timeZone)));
        var insights = _insights.Build(records);
        AverageWordsPerDictationLabel = insights.AverageWordsPerDictation.ToString("0.#");
        AverageDurationLabel = FormatDuration(insights.AverageDurationSeconds);
        InsertionSuccessRateLabel = $"{insights.InsertionSuccessRate:0.#}%";
        PastedCountLabel = insights.PastedCount.ToString();
        TypedCountLabel = insights.TypedCount.ToString();
        ClipboardFallbackCountLabel = insights.CopiedToClipboardCount.ToString();
        FailedInsertionCountLabel = insights.FailedInsertionCount.ToString();
        InsertedBreakdownLabel = Loc.Instance.GetString(
            "Dashboard.InsertedBreakdown",
            insights.PastedCount,
            insights.TypedCount
        );
        CleanupAppliedCountLabel = insights.CleanupAppliedCount.ToString();
        SnippetAppliedCountLabel = insights.SnippetAppliedCount.ToString();
        DictionaryCorrectionAppliedCountLabel =
            insights.DictionaryCorrectionAppliedCount.ToString();
        PromptActionAppliedCountLabel = insights.PromptActionAppliedCount.ToString();
        TranslationAppliedCountLabel = insights.TranslationAppliedCount.ToString();

        RecentActivity.Clear();
        foreach (var r in records.OrderByDescending(r => r.Timestamp).Take(10))
        {
            RecentActivity.Add(
                new DashboardRecentActivityRow(
                    r,
                    PresentationDateTime.ToLocal(r.Timestamp, _timeZone)
                )
            );
        }

        TopApps.Clear();
        foreach (var app in insights.TopApps)
        {
            TopApps.Add(new AppUsageInsightRow(app));
        }

        OnPropertyChanged(nameof(HasTopApps));
        OnPropertyChanged(nameof(HasRecentActivity));
        ScheduleDayRefresh();
    }

    private void ScheduleDayRefresh()
    {
        _dayRefreshTimer?.Dispose();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var midnight = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(now, _timeZone).Date.AddDays(1), DateTimeKind.Unspecified);
        // Some zones advance their clocks at midnight; use the first valid local time.
        while (_timeZone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(1);
        }
        // At fall-back, the larger offset identifies the first occurrence of midnight.
        var midnightUtc = _timeZone.IsAmbiguousTime(midnight)
            ? new DateTimeOffset(midnight, _timeZone.GetAmbiguousTimeOffsets(midnight).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(midnight, _timeZone);
        var due = midnightUtc.AddSeconds(1) - now;
        _dayRefreshTimer = _timeProvider.CreateTimer(
            _ => OnRecordsChanged(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void RefreshStatistics(DateOnly today)
    {
        var length = SelectedRange == TimeRange.Weekly ? 7 : 30;
        var allDays = _statistics.Days.OrderBy(day => day.Day).ToArray();
        var start = SelectedRange == TimeRange.AllTime
            ? allDays.FirstOrDefault()?.Day ?? today
            : today.AddDays(1 - length);
        var end = SelectedRange == TimeRange.AllTime && allDays.LastOrDefault()?.Day > today
            ? allDays[^1].Day
            : today;
        var days = allDays.Where(day => day.Day >= start && day.Day <= end).ToArray();
        WordCount = days.Sum(day => day.TotalWords);
        var seconds = days.Sum(day => day.TotalDurationSeconds);
        var wpm = seconds > 0 ? WordCount / (seconds / 60) : 0;
        AverageWpm = (int)wpm;
        AppCount = days.SelectMany(day => day.AppCounts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        TranscriptionCount = days.Sum(day => day.TranscriptionCount);
        var active = days.Where(day => day.TranscriptionCount > 0 || day.TotalWords > 0 || day.TotalDurationSeconds > 0)
            .Select(day => day.Day).ToHashSet();
        DaysActive = active.Count;
        var run = 0;
        var longest = 0;
        var previous = DateOnly.MinValue;
        foreach (var day in active.Order())
        {
            run = day.DayNumber - previous.DayNumber == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }
        LongestStreak = longest;
        var cursor = active.Contains(today) ? today : today.AddDays(-1);
        var current = 0;
        while (active.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }
        CurrentStreak = current;
        var saved = Math.Max(0, WordCount / ManualTypingWordsPerMinute * 60 - seconds);
        TimeSavedLabel = saved < 60 ? $"{(int)saved}s"
            : saved < 3600 ? $"{(int)(saved / 60)}m" : $"{saved / 3600:F1}h";
        var preceding = allDays.Where(day => day.Day >= start.AddDays(-length) && day.Day < start).ToArray();
        var previousWords = preceding.Sum(day => day.TotalWords);
        var previousSeconds = preceding.Sum(day => day.TotalDurationSeconds);
        WordsTrendLabel = SelectedRange == TimeRange.AllTime ? string.Empty : FormatTrend(WordCount, previousWords);
        WpmTrendLabel = SelectedRange == TimeRange.AllTime ? string.Empty
            : FormatTrend(wpm, previousSeconds > 0 ? previousWords / (previousSeconds / 60) : 0);

        BuildActivityDays(days, start, end);
        BuildBreakdown(Languages, days.Select(day => day.LanguageCounts), code =>
            DictationSectionViewModel.CreateLanguageChoices().FirstOrDefault(option =>
                string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? code.ToUpperInvariant());
        BuildBreakdown(Models, days.Select(day => day.ModelCounts), FormatModelLabel);
        BuildHours(days);
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(HasLanguages));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(CurrentStreakLabel));
        OnPropertyChanged(nameof(LongestStreakLabel));
    }

    private void BuildActivityDays(UsageStatisticsDaySnapshot[] days, DateOnly start, DateOnly today)
    {
        ActivityDays.Clear();
        var dayCount = today.DayNumber - start.DayNumber + 1;
        var bucketSize = SelectedRange == TimeRange.AllTime ? Math.Max(1, (int)Math.Ceiling(dayCount / 48d)) : 1;
        var buckets = days.GroupBy(day => (day.Day.DayNumber - start.DayNumber) / bucketSize)
            .ToDictionary(group => group.Key, group => (Words: group.Sum(day => day.TotalWords), Count: group.Sum(day => day.TranscriptionCount)));
        var maxWords = buckets.Values.Select(bucket => bucket.Words).DefaultIfEmpty().Max();
        for (var index = 0; index < (int)Math.Ceiling(dayCount / (double)bucketSize); index++)
        {
            var bucket = buckets.GetValueOrDefault(index);
            var day = start.AddDays(index * bucketSize);
            var endDay = DateOnly.FromDayNumber(Math.Min(day.DayNumber + bucketSize - 1, today.DayNumber));
            ActivityDays.Add(new DashboardActivityDayRow(day, endDay, bucket.Words, bucket.Count,
                maxWords > 0 ? bucket.Words / (double)maxWords : 0,
                Loc.Instance.GetString("Dashboard.ActivityRow", bucket.Words, bucket.Count)));
        }
    }

    private void BuildHours(UsageStatisticsDaySnapshot[] days)
    {
        var hours = new int[24];
        foreach (var day in days)
        {
            for (var hour = 0; hour < Math.Min(24, day.HourCounts.Count); hour++)
            {
                hours[hour] += day.HourCounts[hour];
            }
        }
        var maxHours = hours.Max();
        Hours.Clear();
        for (var hour = 0; hour < 24; hour++)
        {
            Hours.Add(new DashboardHourRow(
                hour, hours[hour], maxHours > 0 ? hours[hour] / (double)maxHours : 0
            ));
        }
    }

    private static string FormatTrend(double current, double previous) => previous > 0
        ? Loc.Instance.GetString(current >= previous ? "Dashboard.TrendUp" : "Dashboard.TrendDown",
            (int)Math.Abs((current - previous) / previous * 100))
        : string.Empty;

    private string FormatModelLabel(string key)
    {
        var (engine, model) = UsageStatisticsService.ParseModelKey(key);
        var label = _plugins?.TranscriptionEngines.FirstOrDefault(plugin =>
            string.Equals(plugin.ProviderId, engine, StringComparison.OrdinalIgnoreCase))?.ProviderDisplayName ?? engine;
        return string.IsNullOrEmpty(model) ? label : $"{label} · {model}";
    }

    private static void BuildBreakdown(ObservableCollection<DashboardBreakdownRow> target,
        IEnumerable<IReadOnlyDictionary<string, int>> dictionaries, Func<string, string> label)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in dictionaries.SelectMany(dictionary => dictionary))
        {
            counts[pair.Key] = counts.GetValueOrDefault(pair.Key) + pair.Value;
        }
        var total = counts.Values.Sum();
        target.Clear();
        if (total <= 0)
        {
            return;
        }
        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(8))
        {
            target.Add(new DashboardBreakdownRow(
                label(pair.Key), pair.Value, pair.Value * 100d / total, pair.Value / (double)total
            ));
        }
    }

    private void PersistSelectedRange(TimeRange value)
    {
        var encoded = (int)value;
        if (_settings.Current.DashboardSelectedPeriod == encoded)
        {
            return;
        }

        _settings.Update(current => current with { DashboardSelectedPeriod = encoded });
    }

    private static TimeRange ReadSelectedRange(int value)
    {
        return Enum.IsDefined(typeof(TimeRange), value) ? (TimeRange)value : TimeRange.Weekly;
    }

    private static string FormatDuration(double seconds)
    {
        return seconds < 60 ? $"{seconds:0.#}s" : $"{seconds / 60.0:0.#}m";
    }
}

public sealed class DashboardRecentActivityRow
{
    public DashboardRecentActivityRow(TranscriptionRecord record, DateTime localTimestamp)
    {
        Record = record;
        LocalTimestamp = localTimestamp;
    }

    public TranscriptionRecord Record { get; }
    public DateTime LocalTimestamp { get; }
    public string Preview => Record.Preview;
    public string? AppName => Record.AppName;
}

public sealed class AppUsageInsightRow
{
    public AppUsageInsightRow(AppUsageInsight insight)
    {
        AppProcessName = insight.AppProcessName;
        RecordCount = insight.RecordCount;
        WordCount = insight.WordCount;
    }

    public string AppProcessName { get; }
    private int RecordCount { get; }
    private int WordCount { get; }
    public string Summary => Loc.Instance.GetString("Dashboard.SummaryStat", RecordCount, WordCount);
}

public sealed record DashboardActivityDayRow(DateOnly Day, DateOnly EndDay, int WordCount, int TranscriptionCount, double BarWidthFraction, string Label)
{
    public string DateLabel => Day == EndDay ? Day.ToString("d MMM") : $"{Day:d MMM} – {EndDay:d MMM}";
    public double BarWidth => BarWidthFraction * 240;
}

public sealed record DashboardBreakdownRow(string Label, int Count, double Percent, double BarWidthFraction)
{
    public double BarWidth => BarWidthFraction * 240;
}

public sealed record DashboardHourRow(int Hour, int Count, double Intensity)
{
    public string Label => Hour % 6 == 0 ? $"{Hour:00}" : string.Empty;
}