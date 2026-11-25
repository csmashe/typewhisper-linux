using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>Compiled matching rules for one filler word list.</summary>
internal sealed partial class FillerWordMatcher
{
    private const string JapaneseBoundary = @"[\s、。,.!?！？…;:；：—–「』」『（）()\[\]“”""‘’'«»]";

    private static readonly char[] s_horizontalWhitespace = [' ', '\t', '　'];

    private readonly Regex? _latin;
    private readonly Regex? _japanese;
    // \G-anchored, so a lookup never scans the rest of the text.
    private readonly Regex? _japaneseHere;

    internal FillerWordMatcher(IReadOnlyList<string> normalizedWords)
    {
        var latinWords = normalizedWords.Where(static word => !FillerWordFilter.ContainsJapaneseScript(word)).ToList();
        var japaneseWords = normalizedWords.Where(FillerWordFilter.ContainsJapaneseScript).ToList();

        _latin = latinWords.Count == 0 ? null : BuildLatinPattern(latinWords);
        _japanese = japaneseWords.Count == 0 ? null : BuildJapanesePattern(japaneseWords);
        _japaneseHere = _latin is null || _japanese is null ? null : new Regex(@"\G(?:" + _japanese + ")", _japanese.Options);
    }

    /// <summary>Strips the matcher's filler words from <paramref name="text"/>.</summary>
    internal string Apply(string text)
    {
        var result = _latin is null ? text : RemoveFillers(text, _latin, _japaneseHere);
        return _japanese is null ? result : RemoveFillers(result, _japanese, null);
    }

    private static Regex BuildLatinPattern(IReadOnlyList<string> words)
    {
        // Hyphens (ASCII or typographic) join words ("uh-huh", "uh‑oh"); an apostrophe does so
        // only between letters ("um'd"), so a quotation apostrophe still leaves the filler
        // exposed ('Um, hello').
        const string before = @"(?<![\p{L}\p{N}_‐‑-])(?<![\p{L}\p{N}]['’])";
        const string after = @"(?![\p{L}\p{N}_‐‑-])(?!['’][\p{L}\p{N}])";
        // "uh huh", "uh oh" and "uh uh" are answers even without the hyphen; only "uh" gets
        // that exception, on both sides of the pair.
        const string answerGuard = @"(?<!(?<![\p{L}\p{N}_‐‑-])uh[ \t　]+)uh(?![ \t　]+(?:huh|oh|uh)" + after + ")";
        var alternation = string.Join('|', words.Select(static word =>
            word == "uh" ? answerGuard : Regex.Escape(word)));
        var pattern = @"(?<lead>[ \t　]*)" + before + "(?<word>" + alternation + ")" + after + @"(?<gap>[ \t　]*)(?<trail>[,.!?…;:]*)(?<after>[ \t　]*)";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static Regex BuildJapanesePattern(IReadOnlyList<string> words)
    {
        var alternation = string.Join('|', words.Select(static word =>
        {
            var escaped = Regex.Escape(word);

            // "まあ"/"まぁ" must not swallow the leading half of a longer drawl such as "まあまあ".
            return word is "まあ" or "まぁ" ? escaped + "(?!ま[あぁ])" : escaped;
        }));

        // The boundary is a lookbehind rather than a consumed capture so that consecutive
        // fillers ("えっと、あのー、") each still see the separator the previous one left behind.
        const string boundary = "(?<=^|" + JapaneseBoundary + ")";
        // "+" so that fillers written back to back ("えっとあのー") go together.
        var pattern = @"(?<lead>[ \t　]*)" + boundary + "(?:" + alternation + @")+(?<gap>[ \t　]*)(?<trail>[、。,.!?！？…;:；：]*)(?<after>[ \t　]*)";

        return new Regex(pattern, RegexOptions.Compiled);
    }

    // Each removal is settled from its own surroundings instead of a whitespace pass over the
    // whole text, so indentation on untouched lines survives and a sentence-ending mark after a
    // filler ("I agree um. Next") stays with the sentence it closes.
    // A capital owed to the next word skips the fillers that removeNext takes out afterwards.
    private static string RemoveFillers(string text, Regex pattern, Regex? removeNext)
    {
        var matches = pattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var output = new StringBuilder(text.Length);
        var position = 0;
        var capitalOwed = false;
        foreach (Match match in matches)
        {
            AppendRestoringCapital(output, text, position, match.Index, removeNext, ref capitalOwed);
            position = match.Index + match.Length;
            var contentFollows = ContentFollowsOnLine(text, position);
            if (!contentFollows)
            {
                // A capital never carries over to the next line.
                capitalOwed = false;
            }

            if (LineIsBlank(output) && !contentFollows)
            {
                // The line held nothing but fillers: drop the whole line, not just the words.
                TrimLineWhitespace(output);
                position = DropLine(output, text, position);
                continue;
            }

            // A capitalized filler that opened a sentence hands its capital to the next word:
            // "Um, hello" becomes "Hello", "Gut. Äh, ich komme" becomes "Gut. Ich komme".
            var word = match.Groups["word"];
            capitalOwed |= contentFollows && word.Success && char.IsUpper(text, word.Index) && OpensSentence(output);
            output.Append(Replacement(output, match, contentFollows, text, position));
        }

        AppendRestoringCapital(output, text, position, text.Length, removeNext, ref capitalOwed);
        return output.ToString();
    }

    // Only a plain lower-case word takes the owed capital; "@jdoe", "example.com" and "iPhone"
    // keep their casing.
    private static void AppendRestoringCapital(StringBuilder output, string text, int start, int end, Regex? removeNext, ref bool capitalOwed)
    {
        if (capitalOwed)
        {
            var index = start;
            while (index < end)
            {
                if (IsLineSeparator(text[index]))
                {
                    capitalOwed = false;
                    break;
                }

                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    continue;
                }

                // Removing the Latin filler may have taken the Japanese one off its boundary.
                if (removeNext?.Match(text, index) is not { Success: true } next
                    || (index == start && output.Length > 0 && !JapaneseBoundaryRegex().IsMatch(output[^1].ToString())))
                {
                    break;
                }

                // A filler running into the next Latin match ("あの uh") loses its end with it.
                var matchEnd = index + next.Length;
                if (matchEnd > end && !text.AsSpan(end, matchEnd - end).IsWhiteSpace())
                {
                    break;
                }

                index = Math.Min(matchEnd, end);
            }

            if (capitalOwed && index < end)
            {
                capitalOwed = false;
                if (IsPlainLowerCaseWord(text, index, end) && Rune.TryGetRuneAt(text, index, out var first))
                {
                    Span<char> upper = stackalloc char[2];
                    var length = Rune.ToUpperInvariant(first).EncodeToUtf16(upper);
                    output.Append(text, start, index - start);
                    output.Append(upper[..length]);
                    start = index + first.Utf16SequenceLength;
                }
            }
        }

        output.Append(text, start, end - start);
    }

    private static bool IsLineSeparator(char c) => c is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';

    // Letters with inner apostrophes or hyphens, optionally followed by punctuation.
    private static bool IsPlainLowerCaseWord(string text, int index, int end)
    {
        if (!char.IsLower(text, index))
        {
            return false;
        }

        var tokenEnd = index;
        while (tokenEnd < end && !char.IsWhiteSpace(text[tokenEnd]))
        {
            tokenEnd++;
        }

        while (tokenEnd > index && (text[tokenEnd - 1] is ',' or '.' or '!' or '?' or '…' or ';' or ':'
                                    || IsClosingDelimiter(text[tokenEnd - 1])))
        {
            tokenEnd--;
        }

        // By rune, so a supplementary capital (a𐐀) still marks the word as mixed case.
        var position = index;
        while (position < tokenEnd)
        {
            if (!Rune.TryGetRuneAt(text, position, out var rune)
                || Rune.IsUpper(rune)
                || !(Rune.IsLetter(rune) || IsCombiningMark(rune)
                     || rune.Value is '\'' or '’' or '-' or '‐' or '‑'))
            {
                return false;
            }

            position += rune.Utf16SequenceLength;
        }

        return true;
    }

    // Decomposed spellings carry accents as separate marks (e + U+0301).
    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // Whether a word appended to the output would open a sentence: it starts the text or a line,
    // follows sentence-ending punctuation (also inside closing quotes: “Yes.” Um, …), or follows an
    // opening quote or bracket.
    private static bool OpensSentence(StringBuilder output)
    {
        var index = output.Length - 1;
        while (index >= 0 && !IsLineSeparator(output[index]) && char.IsWhiteSpace(output[index]))
        {
            index--;
        }

        if (index < 0 || IsLineSeparator(output[index]))
        {
            return true;
        }

        // Guillemets face either way (« … » in French, » … « in German), so whether one closes a
        // quote comes from pairing: after an odd number of them it closes (« bonjour » Euh, puis …).
        var isGuillemet = output[index] is '»' or '«';
        if (isGuillemet ? !ClosesGuillemetPair(output, index) : IsOpeningDelimiter(output, index))
        {
            return true;
        }

        // French sets a space inside the closing guillemet: « Bonjour. » Euh, …
        while (index > 0 && (IsClosingDelimiter(output[index]) || (isGuillemet && output[index] != '\n' && char.IsWhiteSpace(output[index]))))
        {
            index--;
        }

        return output[index] is '.' or '!' or '?' or '…' or '。' or '！' or '？';
    }

    private static bool ClosesGuillemetPair(StringBuilder output, int index)
    {
        var count = 0;
        for (var position = 0; position < index; position++)
        {
            if (output[position] is '»' or '«')
            {
                count++;
            }
        }

        return count % 2 == 1;
    }

    private static string Replacement(StringBuilder output, Match match, bool contentFollows, string text, int position)
    {
        var closesHere = ClosesHere(text, position);
        // The trail is the whole punctuation run ("...", "?!", ";"): a sentence or clause mark
        // stays with the text before the filler, a comma-only run merely delimited the filler.
        var trail = match.Groups["trail"].Value;
        var endsSentence = trail.Any(static mark =>
            mark is '.' or '!' or '?' or '…' or '。' or '！' or '？' or ';' or ':' or '；' or '：');

        if (!LineHasContent(output))
        {
            // The line's indentation, or the space French puts after «, was captured as the
            // match's lead; keep it once.
            return LineIsEmpty(output) || IsOpeningDelimiter(output, output.Length - 1)
                ? match.Groups["lead"].Value
                : string.Empty;
        }

        if (!contentFollows)
        {
            TrimLineWhitespace(output);
            if (endsSentence)
            {
                return SentenceMark(output, trail);
            }

            // "I agree, um" — the comma only introduced the filler.
            if (IsClauseMark(output[^1]))
            {
                output.Length--;
            }

            return string.Empty;
        }

        if (endsSentence)
        {
            // A previous adjacent filler may have left its separator; the mark closes the sentence.
            TrimLineWhitespace(output);
            return SentenceMark(output, trail) + match.Groups["after"].Value;
        }

        // Re-emit one separator only where the filler was set off by whitespace on either side,
        // so Latin text keeps its word gap and unspaced Japanese text does not gain one, and
        // never against an opening delimiter before it or punctuation after it.
        var whitespace = match.Groups["lead"].Value + match.Groups["gap"].Value + match.Groups["after"].Value;
        if (closesHere)
        {
            // Nothing may sit between the text and its closing mark: neither a separator an
            // earlier adjacent filler left behind nor the comma that introduced this one. A
            // dash pair collapses to one ("I think—um—we"), a spaced dash keeps its space.
            TrimLineWhitespace(output);
            if (!IsDash(text, position))
            {
                if (IsClauseMark(output[^1]))
                {
                    output.Length--;
                }

                return string.Empty;
            }

            if (!EndsWithDash(output))
            {
                return whitespace.Length > 0 && !char.IsWhiteSpace(output[^1]) ? whitespace[..1] : string.Empty;
            }

            output.Length--;
            return string.Empty;
        }

        var separatorFits = !char.IsWhiteSpace(output[^1]) && !IsOpeningDelimiter(output, output.Length - 1);
        // The kind of space the text used comes back (an ideographic space stays ideographic).
        return whitespace.Length > 0 && separatorFits ? whitespace[..1] : string.Empty;
    }

    // The filler's closing mark joins the text before it: nothing when that text already ends a
    // sentence ("Hello. Um." → "Hello."), replacing a trailing comma-class mark ("Hello, um." → "Hello.").
    private static string SentenceMark(StringBuilder output, string trail)
    {
        if (output.Length == 0)
        {
            return trail;
        }

        // Look past closing quotes and brackets: “yes.” already ended its sentence.
        var index = output.Length - 1;
        while (index > 0 && IsClosingDelimiter(output[index]))
        {
            index--;
        }

        if (output[index] is '.' or '!' or '?' or '…' or '。' or '！' or '？')
        {
            return string.Empty;
        }

        if (index == output.Length - 1 && IsClauseMark(output[index]))
        {
            output.Length--;
        }

        return trail;
    }

    // Consumes the line break that follows an emptied line, or the one before it at end of text.
    private static int DropLine(StringBuilder output, string text, int position)
    {
        while (position < text.Length && Array.IndexOf(s_horizontalWhitespace, text[position]) >= 0)
        {
            position++;
        }

        if (position < text.Length && text[position] == '\r')
        {
            position++;
        }

        if (position < text.Length && text[position] == '\n')
        {
            return position + 1;
        }

        if (output.Length > 0 && output[^1] == '\n')
        {
            output.Length -= output.Length > 1 && output[^2] == '\r' ? 2 : 1;
        }

        return position;
    }

    // Punctuation or a closing delimiter right after the match; a straight quote closes only when
    // nothing word-like follows it (otherwise it opens: Say um "hello").
    private static bool ClosesHere(string text, int position)
    {
        if (position >= text.Length)
        {
            return false;
        }

        var next = text[position];
        if (next is '"' or '\'' or '“' or '»' or '«')
        {
            // “ opens English speech but closes German („…“); guillemets face either way.
            return position + 1 >= text.Length || !char.IsLetterOrDigit(text[position + 1]);
        }

        return IsDash(text, position)
            || next is ',' or '.' or ';' or ':' or '!' or '?' or '…' or ')' or ']' or '}' or '”' or '’' or '、' or '。' or '！' or '？' or '」' or '』' or '）';
    }

    // An ASCII hyphen is a dash only when it stands alone between spaces ("I think - um - we").
    private static bool IsDash(string text, int position) =>
        text[position] is '—' or '–'
        || (text[position] == '-' && (position + 1 >= text.Length || char.IsWhiteSpace(text[position + 1])));

    private static bool EndsWithDash(StringBuilder output) =>
        output[^1] is '—' or '–'
        || (output[^1] == '-' && (output.Length == 1 || char.IsWhiteSpace(output[^2])));

    private static bool IsClauseMark(char character) => character is ',' or ';' or ':' or '、' or '；' or '：';

    private static bool IsClosingDelimiter(char character) =>
        character is ')' or ']' or '}' or '"' or '\'' or '”' or '’' or '」' or '』' or '）' or '“' or '»' or '«';

    private static bool ContentFollowsOnLine(string text, int position)
    {
        for (var index = position; index < text.Length && text[index] != '\n'; index++)
        {
            if (Array.IndexOf(s_horizontalWhitespace, text[index]) < 0 && text[index] != '\r')
            {
                return true;
            }
        }

        return false;
    }

    // An opening delimiter starts a fresh segment, so a filler right after one (even mid-line,
    // as in: He said, “Um. Hello”) still counts as starting the text.
    private static bool LineHasContent(StringBuilder output)
    {
        for (var index = output.Length - 1; index >= 0 && output[index] != '\n'; index--)
        {
            if (IsOpeningDelimiter(output, index))
            {
                return false;
            }

            if (!char.IsWhiteSpace(output[index]))
            {
                return true;
            }
        }

        return false;
    }

    // A straight quote, and “ (which closes German „…“ speech), open only when nothing but
    // whitespace or another opener precedes them.
    private static bool IsOpeningDelimiter(StringBuilder output, int index)
    {
        var character = output[index];
        if (character is '(' or '[' or '{' or '„' or '‘' or '「' or '『' or '（')
        {
            return true;
        }

        if (character is not ('"' or '\'' or '“' or '»' or '«'))
        {
            return false;
        }

        return MayOpenQuote(output, index) && !(character == '"' && ClosesStraightQuotePair(output, index));
    }

    // Whatever precedes an opening quote is not a word or a finished sentence: whitespace,
    // another opener, a dash or a clause mark (He said—“Um…”); after those it closes.
    private static bool MayOpenQuote(StringBuilder output, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = output[index - 1];
        return !char.IsLetterOrDigit(previous)
            && previous is not ('.' or '!' or '?' or '…' or '。' or '！' or '？')
            && !IsClosingDelimiter(previous);
    }

    // Straight quotes pair up on their line (He said "hello," Um, friend); one that could not
    // open, like the inch mark in 6'2", stays out of the pairing.
    private static bool ClosesStraightQuotePair(StringBuilder output, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && output[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        var open = false;
        for (var position = lineStart; position < index; position++)
        {
            if (output[position] == '"' && (open || MayOpenQuote(output, position)))
            {
                open = !open;
            }
        }

        return open;
    }

    [GeneratedRegex(JapaneseBoundary)]
    private static partial Regex JapaneseBoundaryRegex();

    private static bool LineIsEmpty(StringBuilder output) =>
        output.Length == 0 || output[^1] == '\n';

    // Unlike LineHasContent, an opening delimiter counts here: "“um" is not a blank line.
    private static bool LineIsBlank(StringBuilder output)
    {
        for (var index = output.Length - 1; index >= 0 && output[index] != '\n'; index--)
        {
            if (!char.IsWhiteSpace(output[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void TrimLineWhitespace(StringBuilder output)
    {
        var length = output.Length;
        while (length > 0 && Array.IndexOf(s_horizontalWhitespace, output[length - 1]) >= 0)
        {
            length--;
        }

        output.Length = length;
    }
}
