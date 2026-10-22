using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.AppImport;

/// <summary>A row of Wispr Flow's <c>Dictionary</c> table.</summary>
public sealed record WisprFlowRow(string Phrase, string? Replacement, bool Deleted, bool Snippet);

/// <summary>Maps Wispr Flow's dictionary (its <c>flow.sqlite</c>, as upstream TypeWhisper #550 reads it) to dictionary entries.</summary>
public static class WisprFlowImport
{
    public const string DatabaseFileName = "flow.sqlite";

    public static AppImportBatch Load(string path, CancellationToken cancellationToken) =>
        Map(WisprFlowDatabase.Read(path, cancellationToken), cancellationToken);

    internal static AppImportBatch Map(IReadOnlyList<WisprFlowRow> rows, CancellationToken cancellationToken)
    {
        var candidates = new List<DictionaryImportCandidate>();
        var excluded = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Deleted || row.Snippet)
            {
                excluded++;
                continue;
            }

            var phrase = row.Phrase.Trim();
            var spelling = string.IsNullOrWhiteSpace(row.Replacement) ? phrase : row.Replacement.Trim();
            candidates.Add(new DictionaryImportCandidate(DictionaryEntryType.Term, spelling));
            if (spelling != phrase)
            {
                candidates.Add(new DictionaryImportCandidate(DictionaryEntryType.Correction, phrase, spelling));
            }
        }

        return new AppImportBatch(candidates, excluded);
    }
}
