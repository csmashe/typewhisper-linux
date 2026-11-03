using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.AppImport;

public enum DictionaryImportOutcome
{
    Add,
    /// <summary>An identical entry already exists, or appeared earlier in the batch.</summary>
    Duplicate,
    /// <summary>An entry with the same text but different settings exists; it is kept unchanged.</summary>
    Conflict,
    /// <summary>Blank, too long, containing control characters, or a correction with a formatting escape.</summary>
    Unsupported,
}

public sealed record DictionaryImportLine(
    DictionaryEntryType Kind,
    string Original,
    string? Replacement,
    DictionaryImportOutcome Outcome
);

public enum DictionaryImportCommitResult
{
    Imported,
    /// <summary>The entries were saved and are in use, but the system could not confirm they are safe from a crash.</summary>
    ImportedNotDurable,
    NothingToImport,
    /// <summary>The dictionary changed after the review was built; nothing was written.</summary>
    Stale,
}

/// <summary>
///     What importing another app's entries would do to the dictionary, computed without writing. Commit it
///     with <see cref="Interfaces.IDictionaryService.CommitReviewedImport" />, which refuses if the dictionary
///     no longer matches <see cref="Baseline" />. Repeating an import yields no additions because entries are
///     matched by content, not by any stored source ID.
/// </summary>
public sealed partial class DictionaryImportReview
{
    private const int MaxTextLength = 160;

    private DictionaryImportReview(
        ImmutableArray<DictionaryEntry> baseline,
        IReadOnlyList<DictionaryImportLine> lines,
        IReadOnlyList<DictionaryEntry> additions,
        int sourceExcluded
    )
    {
        Baseline = baseline;
        Lines = lines;
        Additions = additions;
        SourceExcluded = sourceExcluded;
    }

    public ImmutableArray<DictionaryEntry> Baseline { get; }
    public IReadOnlyList<DictionaryImportLine> Lines { get; }
    public IReadOnlyList<DictionaryEntry> Additions { get; }

    /// <summary>Source rows dropped before review, such as deleted entries; part of the excluded total.</summary>
    public int SourceExcluded { get; }

    public int Count(DictionaryImportOutcome outcome) =>
        Lines.Count(line => line.Outcome == outcome)
        + (outcome == DictionaryImportOutcome.Unsupported ? SourceExcluded : 0);

    public static DictionaryImportReview For(
        AppImportBatch batch,
        IReadOnlyList<DictionaryEntry> current,
        CancellationToken cancellationToken
    )
    {
        // Additions join the comparison set, so a repeat inside the batch is caught like an existing entry.
        var next = current.ToList();
        var lines = new List<DictionaryImportLine>();
        var additions = new List<DictionaryEntry>();
        var now = DateTime.UtcNow;
        foreach (var candidate in batch.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = candidate.Original.Trim();
            var replacement = candidate.Kind == DictionaryEntryType.Correction ? candidate.Replacement?.Trim() : null;
            var entry = new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = candidate.Kind,
                Original = original,
                Replacement = replacement,
                CreatedAt = now,
                Source = DictionaryEntrySource.Import,
            };
            var outcome = IsSupported(entry) ? Classify(entry, next) : DictionaryImportOutcome.Unsupported;
            if (outcome == DictionaryImportOutcome.Add)
            {
                next.Add(entry);
                additions.Add(entry);
            }

            lines.Add(new DictionaryImportLine(candidate.Kind, original, replacement, outcome));
        }

        return new DictionaryImportReview([.. current], lines, additions, batch.Excluded);
    }

    private static DictionaryImportOutcome Classify(DictionaryEntry entry, List<DictionaryEntry> next)
    {
        var collisions = next
            .Where(existing =>
                existing.EntryType == entry.EntryType
                && existing.Original.Trim().Equals(entry.Original, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (collisions.Count == 0)
        {
            return DictionaryImportOutcome.Add;
        }

        // Any setting the user chose differently is a conflict, and the existing entry wins.
        return collisions.All(existing =>
            existing.Replacement == entry.Replacement
            && existing.CaseSensitive == entry.CaseSensitive
            && existing.IsEnabled == entry.IsEnabled
            && !existing.IsRegex
        )
            ? DictionaryImportOutcome.Duplicate
            : DictionaryImportOutcome.Conflict;
    }

    private static bool IsSupported(DictionaryEntry entry) =>
        IsSupportedText(entry.Original)
        && (entry.EntryType != DictionaryEntryType.Correction
            || (IsSupportedText(entry.Replacement) && !FormattingEscape().IsMatch(entry.Replacement!)));

    private static bool IsSupportedText(string? text) =>
        text is { Length: > 0 and <= MaxTextLength } && !text.Any(char.IsControl);

    /// <summary>Matches upstream: a source spelling that looks like a replacement escape is not imported.</summary>
    [GeneratedRegex(@"\\[snrt\\]")]
    private static partial Regex FormattingEscape();

    /// <summary>
    ///     Compares what the user can see or edit, ignoring usage counters that dictation updates in the
    ///     background, so dictating while the review is open does not invalidate it.
    /// </summary>
    internal static bool MatchesBaseline(IReadOnlyList<DictionaryEntry> current, ImmutableArray<DictionaryEntry> baseline)
    {
        return current.Count == baseline.Length
            && current.Zip(baseline).All(pair => WithoutUsage(pair.First) == WithoutUsage(pair.Second));
    }

    private static DictionaryEntry WithoutUsage(DictionaryEntry entry) =>
        entry with
        {
            UsageCount = 0,
            TimesApplied = 0,
            TimesCorrected = 0,
            LastUsedAt = null,
            LastCorrectedAt = null,
        };
}
