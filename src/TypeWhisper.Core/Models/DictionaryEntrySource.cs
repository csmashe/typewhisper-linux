namespace TypeWhisper.Core.Models;

/// <summary>How a <see cref="DictionaryEntry" /> came to exist (entered by hand, imported, accepted from a suggestion, or auto-learned).</summary>
public enum DictionaryEntrySource
{
    Manual,
    Import,
    CorrectionSuggestion,
    AutoLearned
}
