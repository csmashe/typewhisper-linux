namespace TypeWhisper.Core.Models;

/// <summary>
///     A single user-dictionary item: either a vocabulary <c>Term</c> that biases
///     recognition or a find-and-replace <c>Correction</c>, distinguished by
///     <see cref="EntryType" />. Tracks enablement, priority, source, and usage
///     stats.
/// </summary>
public sealed record DictionaryEntry
{
    public required string Id { get; init; }
    public required DictionaryEntryType EntryType { get; init; }
    public required string Original { get; init; }
    public string? Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsStarred { get; init; }

    /// <summary>Total UI/stats reference count (incremented on every interaction, including views).</summary>
    public int UsageCount { get; init; }

    /// <summary>Number of times this entry was actually substituted during correction or vocabulary boosting.</summary>
    public int TimesApplied { get; init; }

    public int TimesCorrected { get; init; }
    public int Priority { get; init; }
    // ReSharper disable once UnusedMember.Global
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; init; }
    public DateTime? LastCorrectedAt { get; init; }
    public DictionaryEntrySource Source { get; init; } = DictionaryEntrySource.Manual;
}