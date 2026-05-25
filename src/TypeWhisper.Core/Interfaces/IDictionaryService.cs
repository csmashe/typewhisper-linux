using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IDictionaryService
{
    IReadOnlyList<DictionaryEntry> Entries { get; }

    void AddEntry(DictionaryEntry entry);
    void AddEntries(IEnumerable<DictionaryEntry> entries);
    void UpdateEntry(DictionaryEntry entry);
    void DeleteEntry(string id);
    void DeleteEntries(IEnumerable<string> ids);

    string ApplyCorrections(string text);
    string? GetTermsForPrompt();

    IReadOnlyList<string> GetEnabledTerms()
    {
        return Entries
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToList();
    }

    void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        throw new NotSupportedException();
    }

    void RemoveAllTerms()
    {
        throw new NotSupportedException();
    }

    void LearnCorrection(string original, string replacement);

    void ActivatePack(TermPack pack);
    void DeactivatePack(string packId);

    string ExportToCsv()
    {
        throw new NotSupportedException();
    }

    int ImportFromCsv(string csv)
    {
        throw new NotSupportedException();
    }

    event Action? EntriesChanged;
}