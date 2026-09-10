namespace TypeWhisper.Core.Models;

/// <summary>
///     A correction learned automatically from an observed edit. Carries the
///     dictionary <see cref="Id" /> that was added or updated so the same batch can
///     be undone later.
/// </summary>
/// <param name="Id">Dictionary entry id that was added or updated.</param>
/// <param name="Original">Original token that was corrected.</param>
/// <param name="Replacement">Replacement token learned for the original token.</param>
public sealed record LearnedDictionaryCorrection(string Id, string Original, string Replacement);
