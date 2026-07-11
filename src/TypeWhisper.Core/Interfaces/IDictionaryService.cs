using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Stores the user's dictionary — spoken-form term boosts and find/replace
///     corrections — and applies them to transcribed text. Backed by a single
///     production implementation; consumers depend on this contract so the store
///     can be faked in tests.
/// </summary>
public interface IDictionaryService
{
    IReadOnlyList<DictionaryEntry> Entries { get; }

    void AddEntry(DictionaryEntry entry);
    // ReSharper disable once UnusedMemberInSuper.Global
    void AddEntries(IEnumerable<DictionaryEntry> entries);
    void UpdateEntry(DictionaryEntry entry);
    void DeleteEntry(string id);
    // ReSharper disable once UnusedMemberInSuper.Global
    void DeleteEntries(IEnumerable<string> ids);

    /// <summary>Applies the enabled corrections to <paramref name="text" /> and returns the rewritten text.</summary>
    string ApplyCorrections(string text);

    /// <summary>Comma-separated enabled terms for seeding an STT/LLM prompt, or <c>null</c> when there are none.</summary>
    string? GetTermsForPrompt();

    /// <summary>Original strings of all enabled term entries (corrections excluded).</summary>
    IReadOnlyList<string> GetEnabledTerms()
    {
        return Entries
            .Where(e => e is { IsEnabled: true, EntryType: DictionaryEntryType.Term })
            .Select(e => e.Original)
            .ToList();
    }

    /// <summary>
    ///     Replaces the enabled term set. When <paramref name="replaceExisting" /> is <c>false</c>,
    ///     existing terms are kept and the new ones merged in.
    /// </summary>
    void SetTerms(IEnumerable<string> terms, bool replaceExisting);

    /// <summary>Removes every term entry, leaving corrections untouched.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    void RemoveAllTerms();

    /// <summary>Removes a single term by its original string; returns <c>true</c> if a term was removed.</summary>
    bool DeleteTerm(string term);

    /// <summary>All correction (find/replace) entries.</summary>
    IReadOnlyList<DictionaryCorrection> GetCorrections();

    /// <summary>Adds or updates a correction keyed by <paramref name="original" />, returning the stored entry.</summary>
    DictionaryCorrection UpsertCorrection(string original, string replacement, bool caseSensitive);

    /// <summary>Removes a correction by its original string; returns <c>true</c> if one was removed.</summary>
    bool DeleteCorrection(string original);

    /// <summary>Records a user-confirmed correction so the same mistake is auto-fixed next time.</summary>
    void LearnCorrection(string original, string replacement);

    /// <summary>
    ///     Silently learns a batch of corrections. New, safe originals are added; an existing
    ///     entry is only ever updated when its id is listed in <paramref name="replaceableEntryIds" />
    ///     (session-created entries the caller is self-healing) — every other existing entry is left
    ///     untouched regardless of source. Returns the entries added or updated so they can be undone.
    /// </summary>
    IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(
        IEnumerable<CorrectionSuggestion> suggestions,
        IReadOnlySet<string>? replaceableEntryIds = null
    );

    /// <summary>Removes correction entries by id (used to undo a learned batch); safe if some no longer exist.</summary>
    void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections);

    /// <summary>Adds the term pack's entries; idempotent, so re-activating an active pack is a no-op.</summary>
    void ActivatePack(TermPack pack);

    /// <summary>Removes all entries that belong to the given term pack.</summary>
    void DeactivatePack(string packId);

    /// <summary>Activates the term pack mapped to an industry preset; a no-op when the preset or its pack is unknown.</summary>
    void ApplyIndustryPreset(string presetId);

    /// <summary>Serializes all entries to CSV.</summary>
    string ExportToCsv();

    /// <summary>Imports entries from CSV, returning the number of entries added.</summary>
    int ImportFromCsv(string csv);

    event Action? EntriesChanged;
}
