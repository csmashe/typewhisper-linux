namespace TypeWhisper.Core.Models;

/// <summary>Whether a <see cref="DictionaryEntry" /> is a recognition-boosting term or a find-and-replace correction.</summary>
public enum DictionaryEntryType
{
    Term,
    Correction
}
