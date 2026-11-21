using System.Text;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

internal enum ElevenLabsEntryKind { Word, Spacing, AudioEvent }

// One entry of a Scribe "words" array. Spacing entries carry no usable times.
internal sealed record ElevenLabsTranscriptEntry(ElevenLabsEntryKind Kind, string Text, double Start, double End);

// Groups Scribe's per-word entries into subtitle-sized segments: a segment ends at a sentence end
// once it has a few words, at a pause longer than a second, or after 30 seconds.
internal static class ElevenLabsSegmentBuilder
{
    private const double PauseThresholdSeconds = 1.0;
    // A sentence end splits only after this many words, so "Yes." joins the next sentence.
    private const int MinimumSentenceWordCount = 3;
    private const double MaximumSegmentSeconds = 30;

    private static readonly char[] s_sentenceEnds = ['.', '!', '?', '…', '。', '！', '？', '؟', '।'];
    private static readonly char[] s_trailingClosers = ['"', '\'', '”', '’', '»', '«', ')', ']', '」', '』'];

    internal static IReadOnlyList<PluginTranscriptionSegment> Build(IReadOnlyList<ElevenLabsTranscriptEntry> entries)
    {
        var segments = new List<PluginTranscriptionSegment>();
        var text = new StringBuilder();
        double? start = null;
        double? end = null;
        var wordCount = 0;
        var lastWordEndsSentence = false;

        foreach (var entry in entries)
        {
            if (entry.Kind == ElevenLabsEntryKind.Spacing)
            {
                text.Append(entry.Text);
                continue;
            }

            if (start is { } segmentStart && end is { } segmentEnd)
            {
                var pause = entry.Start - segmentEnd;
                var tooLong = entry.Start - segmentStart >= MaximumSegmentSeconds;
                var sentenceDone = lastWordEndsSentence && wordCount >= MinimumSentenceWordCount;
                if (pause > PauseThresholdSeconds || sentenceDone || tooLong)
                    Close();
            }

            text.Append(entry.Text);
            start ??= entry.Start;
            end = Math.Max(end ?? entry.End, entry.End);
            if (entry.Kind != ElevenLabsEntryKind.Word)
                continue;

            wordCount++;
            lastWordEndsSentence = EndsSentence(entry.Text);
        }

        Close();
        return segments;

        void Close()
        {
            var trimmed = text.ToString().Trim();
            if (trimmed.Length > 0 && start is { } segmentStart && end is { } segmentEnd)
                segments.Add(new PluginTranscriptionSegment(trimmed, segmentStart, Math.Max(segmentStart, segmentEnd)));
            text.Clear();
            start = null;
            end = null;
            wordCount = 0;
            lastWordEndsSentence = false;
        }
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.Trim().TrimEnd(s_trailingClosers);
        return trimmed.Length > 0 && s_sentenceEnds.Contains(trimmed[^1]);
    }
}
