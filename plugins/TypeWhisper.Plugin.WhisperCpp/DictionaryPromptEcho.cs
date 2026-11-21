using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Recognizes transcripts that may only repeat the dictionary prompt. Without speech, Whisper often returns
/// the terms or pieces of them ("Kwek" for "Kwyjibo"), or markers such as "[BLANK_AUDIO]" it would otherwise drop.
/// </summary>
internal static partial class DictionaryPromptEcho
{
    /// <summary>
    /// Whether the transcript is only non-speech markers, or every word of it matches a prompt word
    /// or starts with the same two letters.
    /// </summary>
    internal static bool IsSuspected(string? text, string prompt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var words = Words(text);
        if (words.Length == 0)
            return NonSpeechMarkerRegex().IsMatch(text);

        var promptWords = Words(prompt);
        return words.All(word => promptWords.Any(promptWord => Resembles(word, promptWord)));
    }

    /// <summary>Whether the transcript contains anything besides punctuation, whitespace and non-speech markers.</summary>
    internal static bool HasWords(string? text) => Words(text).Length > 0;

    private static bool Resembles(string word, string promptWord) =>
        string.Equals(word, promptWord, StringComparison.OrdinalIgnoreCase)
        || word.Length >= 2
        && promptWord.Length >= 2
        && string.Compare(word, 0, promptWord, 0, 2, StringComparison.OrdinalIgnoreCase) == 0;

    private static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        text = NonSpeechMarkerRegex().Replace(text, " ");
        var separators = text.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray();
        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
    }

    // The same markers as the host's WhisperHallucinationFilter.IsNonSpeechAnnotation.
    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)")]
    private static partial Regex NonSpeechMarkerRegex();
}
