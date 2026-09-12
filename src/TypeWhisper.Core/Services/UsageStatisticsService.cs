using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Persists compact daily usage aggregates independently from transcription history.
/// </summary>
public sealed class UsageStatisticsService : IUsageStatisticsService
{
    private const char KeySeparator = '\u001F';

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AtomicJsonStore<UsageStatisticsStore> _store;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IReadOnlyList<TranscriptionRecord>?>? _historyBackfillSource;

    public IReadOnlyList<UsageStatisticsDaySnapshot> Days
    {
        get
        {
            try
            {
                return _store.Current.Days.OrderBy(day => day.Day).Select(ToSnapshot).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.WriteLine($"[UsageStatisticsService] Failed to load statistics: {ex.Message}");
                return [];
            }
        }
    }

    public bool HasAnyStatistics => Days.Any(day =>
        day.TranscriptionCount > 0 || day.TotalWords > 0 || day.TotalDurationSeconds > 0);

    public event Action? StatisticsChanged;

    public UsageStatisticsService(
        string filePath,
        TimeProvider? timeProvider = null,
        Func<IReadOnlyList<TranscriptionRecord>?>? historyBackfillSource = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _historyBackfillSource = historyBackfillSource;
        _store = new AtomicJsonStore<UsageStatisticsStore>(
            filePath,
            static () => new UsageStatisticsStore(),
            new AtomicJsonStoreOptions<UsageStatisticsStore>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = Deserialize,
            }
        );
    }

    public void RecordTranscription(TranscriptionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValid(record))
        {
            return;
        }

        var changed = false;
        try
        {
            if (_historyBackfillSource is not null && !_store.Current.HistoryBackfillCompleted
                && _historyBackfillSource() is { } records)
            {
                changed = BackfillFromHistoryIfNeededCore(records);
            }

            _store.Update(store =>
            {
                if (!store.HistoryBackfillCompleted)
                {
                    return store;
                }
                var updated = CloneStore(store);
                AddToStore(updated, record);
                return updated;
            }, out var recordChanged);
            changed |= recordChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"[UsageStatisticsService] Failed to record transcription: {ex.Message}");
        }
        if (changed)
        {
            StatisticsChanged?.Invoke();
        }
    }

    public void BackfillFromHistoryIfNeeded(IEnumerable<TranscriptionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (BackfillFromHistoryIfNeededCore(records))
        {
            StatisticsChanged?.Invoke();
        }
    }

    private bool BackfillFromHistoryIfNeededCore(IEnumerable<TranscriptionRecord> records)
    {
        bool changed;
        try
        {
            // A caller's lazy sequence must not run under the statistics store lock.
            var materializedRecords = records.ToArray();
            _store.Update(store =>
            {
                if (store.HistoryBackfillCompleted)
                {
                    return store;
                }
                var updated = CloneStore(store);
                foreach (var record in materializedRecords)
                {
                    if (IsValid(record))
                    {
                        AddToStore(updated, record);
                    }
                }
                updated.HistoryBackfillCompleted = true;
                return updated;
            }, out changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"[UsageStatisticsService] Failed to backfill history: {ex.Message}");
            return false;
        }
        return changed;
    }

    private static bool IsValid(TranscriptionRecord record) =>
        record.Status == TranscriptionRecordStatus.Succeeded &&
        record.WordCount > 0 && double.IsFinite(record.DurationSeconds) && record.DurationSeconds >= 0;

    /// <summary>
    /// Builds a stable model aggregation key.
    /// </summary>
    public static string BuildModelKey(string? engineUsed, string? modelUsed)
    {
        var engine = NormalizeValue(engineUsed, "unknown");
        var model = NormalizeValue(modelUsed, string.Empty);
        return string.Concat(engine, KeySeparator, model);
    }

    /// <summary>
    /// Splits a model aggregation key into its original components.
    /// </summary>
    public static (string EngineUsed, string? ModelUsed) ParseModelKey(string key)
    {
        var parts = key.Split(KeySeparator, 2);
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    private void AddToStore(UsageStatisticsStore store, TranscriptionRecord record)
    {
        var utc = record.Timestamp.Kind == DateTimeKind.Local
            ? record.Timestamp.ToUniversalTime()
            : DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc);
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(utc, _timeProvider.LocalTimeZone);
        var localDay = DateOnly.FromDateTime(localTimestamp);
        var day = store.Days.FirstOrDefault(candidate => candidate.Day == localDay);
        if (day is null)
        {
            day = new UsageStatisticsDayData { Day = localDay };
            store.Days.Add(day);
        }

        day.TranscriptionCount++;
        day.TotalWords += record.WordCount;
        day.TotalDurationSeconds += record.DurationSeconds;

        var appKey = NormalizeValue(record.AppProcessName, NormalizeValue(record.AppName, string.Empty));
        if (appKey.Length > 0)
        {
            day.AppCounts[appKey] = day.AppCounts.GetValueOrDefault(appKey) + 1;
        }

        var modelKey = BuildModelKey(record.EngineUsed, record.ModelUsed);
        day.ModelCounts[modelKey] = day.ModelCounts.GetValueOrDefault(modelKey) + 1;

        var primarySubtag = NormalizeValue(record.Language, "unknown").Split('-')[0];
        var languageKey = NormalizeValue(primarySubtag, "unknown").ToLowerInvariant();
        day.LanguageCounts[languageKey] = day.LanguageCounts.GetValueOrDefault(languageKey) + 1;

        NormalizeHours(day);
        day.HourCounts[localTimestamp.Hour]++;
    }

    private static UsageStatisticsStore Deserialize(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Usage statistics must be an object.");
        if (root["days"] is JsonArray days)
        {
            foreach (var day in days)
            {
                if (day is not JsonObject entry || entry["day"] is not JsonValue dayValue
                    || !dayValue.TryGetValue<string>(out var value))
                {
                    throw new JsonException("Each usage statistics entry must contain a calendar day.");
                }
                if (value is { Length: > 10 } && value[10] == 'T')
                {
                    // Legacy timestamps represent a calendar day, irrespective of their offset.
                    value = value[..10];
                }
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                {
                    throw new JsonException("Usage statistics contains an invalid calendar day.");
                }
                entry["day"] = value;
            }
        }
        var store = root.Deserialize<UsageStatisticsStore>(s_jsonOptions)
            ?? throw new JsonException("Usage statistics deserialized to null.");
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- JSON can carry null here
        store.Days ??= [];
        foreach (var day in store.Days)
        {
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- JSON can carry null here
            day.AppCounts = new Dictionary<string, int>(day.AppCounts ?? [], StringComparer.OrdinalIgnoreCase);
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- JSON can carry null here
            day.ModelCounts = new Dictionary<string, int>(day.ModelCounts ?? [], StringComparer.OrdinalIgnoreCase);
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- JSON can carry null here
            day.LanguageCounts = new Dictionary<string, int>(day.LanguageCounts ?? [], StringComparer.OrdinalIgnoreCase);
            NormalizeHours(day);
        }
        return store;
    }

    private static UsageStatisticsStore CloneStore(UsageStatisticsStore store) => new()
    {
        Version = store.Version,
        HistoryBackfillCompleted = store.HistoryBackfillCompleted,
        Days = store.Days.Select(day => new UsageStatisticsDayData
        {
            Day = day.Day,
            TranscriptionCount = day.TranscriptionCount,
            TotalWords = day.TotalWords,
            TotalDurationSeconds = day.TotalDurationSeconds,
            AppCounts = new Dictionary<string, int>(day.AppCounts, StringComparer.OrdinalIgnoreCase),
            ModelCounts = new Dictionary<string, int>(day.ModelCounts, StringComparer.OrdinalIgnoreCase),
            LanguageCounts = new Dictionary<string, int>(day.LanguageCounts, StringComparer.OrdinalIgnoreCase),
            HourCounts = [.. day.HourCounts],
        }).ToList(),
    };

    private static UsageStatisticsDaySnapshot ToSnapshot(UsageStatisticsDayData day) => new()
    {
        Day = day.Day,
        TranscriptionCount = day.TranscriptionCount,
        TotalWords = day.TotalWords,
        TotalDurationSeconds = day.TotalDurationSeconds,
        AppCounts = new Dictionary<string, int>(day.AppCounts, StringComparer.OrdinalIgnoreCase),
        ModelCounts = new Dictionary<string, int>(day.ModelCounts, StringComparer.OrdinalIgnoreCase),
        LanguageCounts = new Dictionary<string, int>(day.LanguageCounts, StringComparer.OrdinalIgnoreCase),
        HourCounts = [.. day.HourCounts],
    };

    private static string NormalizeValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void NormalizeHours(UsageStatisticsDayData day)
    {
        if (day.HourCounts is { Length: 24 })
        {
            return;
        }

        var hours = new int[24];
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- JSON can carry null here
        if (day.HourCounts is not null)
        {
            Array.Copy(day.HourCounts, hours, Math.Min(day.HourCounts.Length, hours.Length));
        }
        day.HourCounts = hours;
    }

    private sealed class UsageStatisticsStore
    {
        public int Version { get; init; } = 1;
        public bool HistoryBackfillCompleted { get; set; }
        public List<UsageStatisticsDayData> Days { get; set; } = [];
    }

    private sealed class UsageStatisticsDayData
    {
        public DateOnly Day { get; init; }
        public int TranscriptionCount { get; set; }
        public int TotalWords { get; set; }
        public double TotalDurationSeconds { get; set; }
        public Dictionary<string, int> AppCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ModelCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> LanguageCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int[] HourCounts { get; set; } = new int[24];
    }
}
