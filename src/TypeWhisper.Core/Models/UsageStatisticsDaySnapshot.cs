namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents the retention-independent usage aggregates for one local calendar day.
/// </summary>
public sealed record UsageStatisticsDaySnapshot
{
    /// <summary>
    /// The local calendar date at recording time, preserved across later time zone changes.
    /// </summary>
    public required DateOnly Day { get; init; }

    /// <summary>
    /// The number of valid transcriptions recorded on this day.
    /// </summary>
    public int TranscriptionCount { get; init; }

    /// <summary>
    /// The total word count of transcriptions recorded on this day.
    /// </summary>
    public int TotalWords { get; init; }

    /// <summary>
    /// The total transcription audio duration in seconds for this day.
    /// </summary>
    public double TotalDurationSeconds { get; init; }

    /// <summary>
    /// Transcription counts by app process name, falling back to app name, with case-insensitive keys.
    /// </summary>
    public IReadOnlyDictionary<string, int> AppCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transcription counts by engine and model key, separated by U+001F, with case-insensitive keys.
    /// </summary>
    public IReadOnlyDictionary<string, int> ModelCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transcription counts by normalized primary language tag, using "unknown" when absent.
    /// </summary>
    public IReadOnlyDictionary<string, int> LanguageCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transcription counts for each local hour, indexed from 0 through 23 at recording time.
    /// </summary>
    public IReadOnlyList<int> HourCounts { get; init; } = new int[24];
}