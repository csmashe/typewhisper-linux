namespace TypeWhisper.Core.Models;

public sealed record DictionaryEntry
{
    public required string Id { get; init; }
    public required DictionaryEntryType EntryType { get; init; }
    public required string Original { get; init; }
    public string? Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsStarred { get; init; }
    /// <summary>Total number of times this entry has been referenced for display and usage stats.</summary>
    public int UsageCount { get; init; }
    /// <summary>Number of times this entry has been applied by dictionary correction or term-ranking logic.</summary>
    public int TimesApplied { get; init; }
    public int TimesCorrected { get; init; }
    public int Priority { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; init; }
    public DateTime? LastCorrectedAt { get; init; }
    public DictionaryEntrySource Source { get; init; } = DictionaryEntrySource.Manual;
}

public enum DictionaryEntryType
{
    Term,
    Correction
}

public enum DictionaryEntrySource
{
    Manual,
    Import,
    CorrectionSuggestion,
    AutoLearned
}
