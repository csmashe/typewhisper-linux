using System.Text;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Recognizes the stock caption phrases Whisper-family models emit on silent audio ("Thank
///     you.", "Thanks for watching", …). The model reports these with high confidence, so the
///     no-speech gate misses them. Fires only when such a phrase is the entire transcript of a
///     short clip, leaving ordinary dictation that merely contains the words untouched.
/// </summary>
public static class WhisperHallucinationFilter
{
    // Longer than this is likely real speech even if it's a stock phrase.
    private const double MaxHallucinationClipSeconds = 2.5;

    // Below this no-speech probability the engine is confident there WAS speech, so a stock
    // phrase is treated as a real (if terse) dictation rather than a silence artifact.
    private const float MinNoSpeechProbability = 0.3f;

    // Normalized (lowercased, punctuation-stripped, single-spaced) stock outputs.
    private static readonly HashSet<string> s_phrases = new(StringComparer.Ordinal)
    {
        "thank you",
        "thank you very much",
        "thank you so much",
        "thanks for watching",
        "thank you for watching",
        "thanks for watching everyone",
        "please subscribe",
        "please subscribe to my channel",
        "like and subscribe",
        "see you next time",
        "see you in the next video",
        "i'll see you in the next video",
        "bye",
        "bye bye",
        "goodbye",
        "you"
    };

    /// <summary>
    ///     True when <paramref name="transcript" /> is, in its entirety, a known Whisper
    ///     silence-artifact, the clip is short enough that real speech is unlikely, and the engine
    ///     is not confident speech was present. A <paramref name="noSpeechProbability" /> below
    ///     <see cref="MinNoSpeechProbability" /> means the engine heard confident speech, so a
    ///     deliberately terse dictation ("Thank you.") is kept; a null probability means the engine
    ///     reported nothing, so the phrase-and-duration test alone applies.
    /// </summary>
    public static bool IsLikelyHallucination(
        string? transcript,
        double durationSeconds,
        float? noSpeechProbability
    )
    {
        if (string.IsNullOrWhiteSpace(transcript) || durationSeconds > MaxHallucinationClipSeconds)
        {
            return false;
        }

        // Kept as an early-return guard: merging into the final return forces a `is not <`
        // double-negative that reads worse than "confident speech → not a hallucination".
        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (noSpeechProbability is < MinNoSpeechProbability)
        {
            return false;
        }

        return s_phrases.Contains(Normalize(transcript));
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true; // trims leading space and collapses runs
        foreach (var ch in value.Trim())
        {
            if (char.IsLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (ch is '\'' or '’')
            {
                // Keep apostrophes so contractions ("i'll") match their listed form.
                builder.Append('\'');
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch) && !lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
