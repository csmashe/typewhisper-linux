namespace TypeWhisper.Plugin.GemmaLocal;

/// <summary>One prompt piece; only trusted template pieces are tokenized with special tokens.</summary>
internal readonly record struct GemmaPromptSegment(string Text, bool IsTemplate);

internal static class GemmaChatFormat
{
    internal const string TurnStart = "<|turn>";
    internal const string TurnEnd = "<turn|>";
    internal const string ChannelStart = "<|channel>";
    internal const string ChannelEnd = "<channel|>";
    internal const string EndOfSequence = "<eos>";

    internal const string OutputHygieneInstruction =
        "Output ONLY the requested result, nothing else. No explanations, no extra text.";

    // Some GGUF vocabularies do not flag every turn-end marker as end of generation.
    internal static IReadOnlyList<string> StopMarkers { get; } = [TurnEnd, EndOfSequence];

    /// <summary>
    ///     Builds the Gemma 4 GGUF chat template's prompt with thinking disabled. Caller text is
    ///     a separate non-template segment, so a typed <c>&lt;turn|&gt;</c> stays literal text
    ///     instead of closing the turn.
    /// </summary>
    internal static IReadOnlyList<GemmaPromptSegment> Format(string systemPrompt, string userText)
    {
        var segments = new List<GemmaPromptSegment>(6);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            segments.Add(new GemmaPromptSegment(TurnStart + "system\n", true));
            segments.Add(new GemmaPromptSegment(systemPrompt.Trim() + "\n" + OutputHygieneInstruction, false));
            segments.Add(new GemmaPromptSegment(TurnEnd + "\n", true));
        }

        segments.Add(new GemmaPromptSegment(TurnStart + "user\n", true));
        segments.Add(new GemmaPromptSegment(userText.Trim(), false));
        segments.Add(new GemmaPromptSegment(TurnEnd + "\n" + TurnStart + "model\n", true));
        return segments;
    }

    /// <summary>Gemma 4 control markers are pipe-delimited: <c>&lt;|channel&gt;</c>, <c>&lt;channel|&gt;</c>.</summary>
    internal static bool IsControlMarker(string text) =>
        text.Length >= 4
        && ((text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith('>'))
            || (text.StartsWith('<') && text.EndsWith("|>", StringComparison.Ordinal)));

    /// <summary>
    ///     Cuts caller text one character into every occurrence of <paramref name="markers" />.
    ///     llama.cpp matches user-defined tokens even when special parsing is off, so tokenizing
    ///     the pieces separately is what keeps a typed marker literal.
    /// </summary>
    internal static IReadOnlyList<string> SplitLiteralMarkers(string text, IReadOnlyCollection<string> markers)
    {
        var cuts = new SortedSet<int>();
        foreach (var marker in markers)
        {
            for (var start = text.IndexOf(marker, StringComparison.Ordinal);
                 start >= 0;
                 start = text.IndexOf(marker, start + 1, StringComparison.Ordinal))
                cuts.Add(start + 1);
        }

        if (cuts.Count == 0)
            return [text];

        var pieces = new List<string>(cuts.Count + 1);
        var previous = 0;
        foreach (var cut in cuts)
        {
            pieces.Add(text[previous..cut]);
            previous = cut;
        }

        pieces.Add(text[previous..]);
        return pieces;
    }
}

/// <summary>
///     Drops tokens inside a <c>&lt;|channel&gt;…&lt;channel|&gt;</c> reasoning block. Gemma 4 can
///     emit one (often empty) even with thinking disabled. Works on token IDs, so the same
///     characters generated as ordinary text are kept.
/// </summary>
internal sealed class GemmaChannelFilter(int channelStart, int channelEnd)
{
    internal bool InChannel { get; private set; }

    internal bool Admit(int token)
    {
        if (token == channelStart)
        {
            InChannel = true;
            return false;
        }

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (token == channelEnd)
        {
            InChannel = false;
            return false;
        }

        return !InChannel;
    }
}
