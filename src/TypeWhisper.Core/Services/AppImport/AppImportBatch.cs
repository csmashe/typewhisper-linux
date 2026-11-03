using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.AppImport;

/// <summary>Why another app's data could not be read for import.</summary>
public enum AppImportReadFailure
{
    /// <summary>The source kept changing while it was read, usually because its app was saving.</summary>
    Unstable,
    TooLarge,
    TooManyEntries,
    /// <summary>Not data the source app writes: wrong shape, types or schema.</summary>
    Malformed,
    /// <summary>The system SQLite library needed to read a database is not installed.</summary>
    SqliteUnavailable,
}

public sealed class AppImportReadException(AppImportReadFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AppImportReadFailure Failure { get; } = failure;
}

/// <summary>One dictionary entry another app holds, before review; <c>Replacement</c> is set only for corrections.</summary>
public sealed record DictionaryImportCandidate(DictionaryEntryType Kind, string Original, string? Replacement = null);

/// <summary>
///     What a source yielded for review. <see cref="Excluded" /> counts source rows dropped before review
///     (deleted entries or another app's snippets), which the review summary reports as excluded.
/// </summary>
public sealed record AppImportBatch(IReadOnlyList<DictionaryImportCandidate> Candidates, int Excluded = 0);
