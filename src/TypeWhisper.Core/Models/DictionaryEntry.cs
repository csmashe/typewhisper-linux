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
    public int UsageCount { get; init; }
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
