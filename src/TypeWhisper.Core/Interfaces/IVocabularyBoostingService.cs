namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Rewrites transcribed text to favor the user's dictionary terms, correcting
///     near-misses produced by the STT engine (brand names, jargon, and the like).
/// </summary>
public interface IVocabularyBoostingService
{
    /// <summary>Returns <paramref name="rawText" /> with recognized dictionary terms substituted in.</summary>
    string Apply(string rawText);
}
