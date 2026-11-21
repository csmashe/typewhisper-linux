using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>One word with its time in seconds, as Reson8 returns it with <c>include_words</c>.</summary>
internal sealed record Reson8Word(string Text, double Start, double End);

/// <summary>Splits Reson8's words into subtitle segments with the macOS plugin's rules.</summary>
internal static class Reson8Transcript
{
    /// <summary>A silence longer than this starts a new segment.</summary>
    private const double PauseThreshold = 1.0;

    /// <summary>A sentence end splits only after this many words, so "Yes." joins the next sentence.</summary>
    private const int MinimumSentenceWordCount = 3;

    /// <summary>Without a sentence end or pause, a segment is split at the next word past this length.</summary>
    private const double MaximumSegmentDuration = 30;

    private static readonly char[] s_sentenceEnds = ['.', '!', '?', '…', '。', '！', '？', '؟', '।'];
    private static readonly char[] s_trailingClosers = ['"', '\'', '”', '’', '»', '«', ')', ']', '」', '』'];

    internal static IReadOnlyList<Reson8Word> Words(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Reson8Word>();
        foreach (var word in words.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.Object
                || !word.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || textElement.GetString() is not { Length: > 0 } text
                || !TryGetDouble(word, "start_ms", out var start)
                || !TryGetDouble(word, "duration_ms", out var duration)
                || start < 0
                || duration < 0)
            {
                continue;
            }

            result.Add(new Reson8Word(text, start / 1000.0, (start + duration) / 1000.0));
        }

        return result;
    }

    // A segment's text is the stretch of the transcript its words span, so languages written without
    // spaces keep none; words the transcript doesn't contain in order are joined with a space.
    internal static IReadOnlyList<PluginTranscriptionSegment> SentenceSegments(IReadOnlyList<Reson8Word> words, string text = "")
    {
        var segments = new List<PluginTranscriptionSegment>();
        var current = new List<Reson8Word>();
        var spans = WordSpans(words, text);
        var index = 0;

        foreach (var word in words)
        {
            if (current.Count > 0
                && (word.Start - current[^1].End > PauseThreshold || word.End - current[0].Start > MaximumSegmentDuration))
            {
                Close();
            }

            current.Add(word);
            index++;
            if (current.Count >= MinimumSentenceWordCount && EndsSentence(word.Text))
                Close();
        }

        Close();
        return segments;

        void Close()
        {
            if (current.Count == 0)
                return;

            var first = index - current.Count;
            var cue = spans is null
                ? string.Join(" ", current.Select(word => word.Text.Trim()))
                : text[spans[first].Start..spans[index - 1].End].Trim();
            segments.Add(new PluginTranscriptionSegment(cue, current[0].Start, Math.Max(current[0].Start, current[^1].End)));
            current.Clear();
        }
    }

    // Where each word sits in the transcript, or null when one can't be found after the previous.
    private static (int Start, int End)[]? WordSpans(IReadOnlyList<Reson8Word> words, string text)
    {
        var spans = new (int Start, int End)[words.Count];
        var cursor = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Text.Trim();
            var start = word.Length == 0 ? -1 : text.IndexOf(word, cursor, StringComparison.Ordinal);
            if (start < 0)
                return null;

            cursor = start + word.Length;
            spans[i] = (start, cursor);
        }

        return spans;
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.TrimEnd().TrimEnd(s_trailingClosers);
        return trimmed.Length > 0 && s_sentenceEnds.Contains(trimmed[^1]);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }
}
