using System.Text;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Whisper's initial prompt, limited to 224 UTF-8 bytes because Whisper.net has no tokenizer and each
/// byte-level token covers at least one byte. whisper.cpp keeps only the last tokens of a longer prompt.
/// </summary>
/// <param name="Value">The prompt Whisper decodes with.</param>
/// <param name="Text">The caller's prompt text alone, for a decode without dictionary terms.</param>
/// <param name="HasTerms">Whether <paramref name="Value" /> contains a dictionary term.</param>
internal sealed record WhisperPrompt(string Value, string? Text, bool HasTerms)
{
    internal const int MaxBytes = 224;

    /// <summary>
    /// The caller's text first, then whole terms in order while they fit; text that alone exceeds the
    /// window is kept as is. Null when there is neither.
    /// </summary>
    internal static WhisperPrompt? Create(string? prompt)
    {
        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        var kept = new StringBuilder(parsed.Text);
        var bytes = Encoding.UTF8.GetByteCount(kept.ToString());
        var hasTerms = false;
        foreach (var term in parsed.DictionaryTerms)
        {
            var separator = kept.Length == 0 ? "" : hasTerms ? ", " : "\n";
            var cost = Encoding.UTF8.GetByteCount(separator) + Encoding.UTF8.GetByteCount(term);
            if (bytes + cost > MaxBytes)
                break;

            kept.Append(separator).Append(term);
            bytes += cost;
            hasTerms = true;
        }

        return kept.Length == 0 ? null : new WhisperPrompt(kept.ToString(), parsed.Text, hasTerms);
    }
}
