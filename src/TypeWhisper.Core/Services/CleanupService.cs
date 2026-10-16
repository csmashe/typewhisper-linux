using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Deterministic transcript cleanup: strips fillers and edge noise, applies spoken
///     punctuation/backtrack/list formatting and sentence casing. Higher cleanup levels
///     (Medium/High) supply the LLM system prompt used by the provider-backed pass.
/// </summary>
public sealed partial class CleanupService
{
    public const string MediumSystemPrompt =
        "Improve readability while preserving meaning, facts, tone, and terminology. Remove filled pauses and accidental repetitions. Resolve explicit self-corrections and repair markers (such as scratch that, no wait, never mind, cancel that, I mean, actually) by dropping the retracted phrase or instruction completely and keeping only the corrected intent. Preserve names, numbers, negation, code, and quoted text. Preserve meaningful uses of words such as like, well, and you know. Do not invent endings for unfinished thoughts. Do not add new information. Return only the cleaned text.";

    public const string HighSystemPrompt =
        "Rewrite as concise polished prose while preserving meaning, facts, tone, and terminology. Remove filled pauses and accidental repetitions. Resolve explicit self-corrections and repair markers (such as scratch that, no wait, never mind, cancel that, I mean, actually) by dropping the retracted phrase or instruction completely and keeping only the corrected intent. Preserve names, numbers, negation, code, and quoted text. Preserve meaningful uses of words such as like, well, and you know. Do not invent endings for unfinished thoughts. Do not add new information. Return only the rewritten text.";

    // Common filler/connective words that shouldn't seed a single-word bullet list. Cached once
    // (OrdinalIgnoreCase) because IsConservativeSingleWordBulletList probes it per candidate word.
    private static readonly HashSet<string> s_bulletListStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "for",
        "need",
        "of",
        "or",
        "the",
        "things",
        "to",
        "we",
        "with",
    };

    private static readonly HashSet<string> s_determinerOrVerbStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "the",
        "this",
        "that",
        "to",
        "please",
        "just",
        "dont",
        "don't",
    };

    // kept instance: injected as a DI/test seam by callers
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string Clean(
        string text,
        CleanupLevel level,
        string? sourceLanguage = null,
        ProfileStylePreset? stylePreset = null
    )
    {
        if (level == CleanupLevel.None || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return level switch
        {
            CleanupLevel.Light => CleanLight(text, sourceLanguage, stylePreset),
            // The Linux service adds the provider-backed pass after these local rules.
            CleanupLevel.Medium or CleanupLevel.High => CleanLight(text, sourceLanguage, stylePreset),
            _ => text,
        };
    }

    public static string GetLlmSystemPrompt(CleanupLevel level, ProfileStylePreset? stylePreset = null)
    {
        // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
        var basePrompt = level switch
        {
            CleanupLevel.Medium => MediumSystemPrompt,
            CleanupLevel.High => HighSystemPrompt,
            _ => throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Only Medium and High cleanup use LLM prompts."
            ),
        };

        return stylePreset switch
        {
            ProfileStylePreset.FormalEmail => basePrompt
                + " Format as a readable email with finished sentences, a comma and paragraph break after a greeting (such as 'Hi Name,'), question marks on questions, and a paragraph break before a closing sign-off (such as 'Thanks.'), without inventing a subject line or sender name.",
            ProfileStylePreset.CasualMessage => basePrompt
                + " Keep a natural, casual chat tone with clean sentence punctuation.",
            ProfileStylePreset.MeetingNotes => basePrompt
                + " When the speaker dictates an explicit list (such as 'first... second... third...'), format it as a clean numbered or bulleted list without adding extra items.",
            _ => basePrompt,
        };
    }

    private static string CleanLight(
        string text,
        string? sourceLanguage,
        ProfileStylePreset? stylePreset = null
    )
    {
        var cleaned = text.Trim();
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        var literals = LiteralRegex().Matches(cleaned).Concat(AnswerPhraseRegex().Matches(cleaned)).ToArray();
        var english = sourceLanguage?.Split('-')[0].Equals("en", StringComparison.OrdinalIgnoreCase) == true;
        cleaned = StandaloneFillerRegex().Replace(cleaned, match =>
        {
            var word = match.Groups["word"].Value;
            if (literals.Any(literal => match.Index < literal.Index + literal.Length
                                        && match.Index + match.Length > literal.Index)
                || word == word.ToUpperInvariant()
                || (!english && (word.Equals("um", StringComparison.OrdinalIgnoreCase)
                                  || word.Equals("er", StringComparison.OrdinalIgnoreCase))))
                return match.Value;
            return match.Groups["prefix"].Value;
        });
        cleaned = ApplyBacktrack(cleaned);
        cleaned = ApplySpokenPunctuation(cleaned);
        cleaned = ApplySmartFormatting(cleaned, stylePreset);
        // Re-strip leading/trailing noise because spoken-punctuation and backtrack transforms
        // can introduce new noise at the edges (e.g. a leading comma after "scratch that").
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = WhitespaceBeforeNewlineRegex().Replace(cleaned, "\n");
        cleaned = WhitespaceBeforePunctuationRegex().Replace(cleaned, "$1");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = RepeatedInlineWhitespaceRegex().Replace(cleaned, " ").Trim();
        return ApplyBasicSentenceCasing(cleaned);
    }

    private static string ApplyBacktrack(string text)
    {
        var cleaned = ScratchThatWholeUtteranceRegex().Replace(text, "");
        cleaned = ApplyRepairMarkerReplacement(cleaned);
        cleaned = OneWordCorrectionRegex().Replace(cleaned, "${prefix}${replacement}${suffix}");
        return cleaned;
    }

    private static string ApplyRepairMarkerReplacement(string text)
    {
        return RepairMarkerReplacementRegex().Replace(text, match =>
        {
            var clause = match.Groups["clause"].Value;
            var words = WordRegex().Matches(clause);
            if (words.Count < 2)
            {
                return match.Value;
            }

            var marker = match.Groups["marker"].Value;
            var lastWord = words[^1].Value;
            if (marker.StartsWith("scratch", StringComparison.OrdinalIgnoreCase)
                && s_determinerOrVerbStopWords.Contains(lastWord))
            {
                return match.Value;
            }

            var replacement = match.Groups["replacement"].Value;
            if (match.Index > 0)
            {
                var before = text[..match.Index].TrimEnd();
                if (before.Length > 0 && before[^1] is '.' or '!' or '?' or '\n')
                {
                    replacement = ApplyBasicSentenceCasing(replacement);
                }
            }

            return replacement;
        });
    }

    private static string ApplySmartFormatting(string text, ProfileStylePreset? stylePreset = null)
    {
        var bullets = TryFormatSpokenBulletList(text);
        if (bullets is not null)
        {
            return bullets;
        }

        var numbered = TryFormatSpokenNumberedList(text);
        if (numbered is not null)
        {
            return numbered;
        }

        if (stylePreset == ProfileStylePreset.FormalEmail)
        {
            var email = TryFormatFormalEmail(text);
            if (email is not null)
            {
                return email;
            }
        }

        return text;
    }

    private static string ApplySpokenPunctuation(string text)
    {
        return SpokenPunctuationRegex()
            .Replace(
                text,
                match =>
                {
                    var mark = match.Groups["mark"].Value.ToLowerInvariant();
                    if (!ShouldApplySpokenPunctuation(text, match, mark))
                    {
                        return match.Value;
                    }

                    return mark switch
                    {
                        "comma" => ",",
                        "period" or "full stop" => ".",
                        "question mark" => "?",
                        "exclamation mark" or "exclamation point" => "!",
                        "colon" => ":",
                        "semicolon" => ";",
                        _ => match.Value,
                    };
                }
            );
    }

    private static bool ShouldApplySpokenPunctuation(string text, Match match, string mark)
    {
        var previousWordCount = CountWordsBefore(text, match.Index);
        var hasWordAfter = HasWordAfter(text, match.Index + match.Length);

        return mark switch
        {
            "period" or "full stop" => previousWordCount >= 2 && !hasWordAfter,
            "comma" or "colon" or "semicolon" => previousWordCount >= 1 && hasWordAfter,
            "question mark" or "exclamation mark" or "exclamation point" => previousWordCount >= 1
                                                                            && !hasWordAfter,
            _ => false,
        };
    }

    private static int CountWordsBefore(string text, int endIndex)
    {
        var prefix = text[..endIndex];
        var boundary = Math.Max(
            Math.Max(prefix.LastIndexOf('.'), prefix.LastIndexOf('?')),
            Math.Max(prefix.LastIndexOf('!'), prefix.LastIndexOf('\n'))
        );
        var phrase = prefix[(boundary + 1)..];
        return WordRegex().Count(phrase);
    }

    private static bool HasWordAfter(string text, int startIndex)
    {
        return WordRegex().IsMatch(text[startIndex..]);
    }

    private static string? TryFormatSpokenBulletList(string text)
    {
        var match = BulletListTriggerRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var body = match.Groups["items"].Value.Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
        if (body.Length == 0)
        {
            return null;
        }

        var items = SplitBulletItems(body);
        return items.Count < 2 ? null : string.Join('\n', items.Select(item => $"- {item}"));
    }

    private static List<string> SplitBulletItems(string body)
    {
        var explicitItems = ExplicitBulletSeparatorRegex()
            .Split(body)
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();
        if (explicitItems.Count >= 2)
        {
            return explicitItems;
        }

        var punctuatedItems = BulletPunctuationSeparatorRegex()
            .Split(body)
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();
        if (punctuatedItems.Count >= 2)
        {
            return punctuatedItems;
        }

        var words = body.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();

        return IsConservativeSingleWordBulletList(words) ? words : [];
    }

    private static bool IsConservativeSingleWordBulletList(List<string> words)
    {
        if (words.Count is < 2 or > 9)
        {
            return false;
        }

        return words.All(word =>
            SingleWordListItemRegex().IsMatch(word)
            && !s_bulletListStopWords.Contains(word)
        );
    }

    private static string CleanListItem(string item)
    {
        return item.Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
    }

    private static string? TryFormatSpokenNumberedList(string text)
    {
        var ordinal = TryFormatOrdinalNumberedList(text);
        if (ordinal is not null)
        {
            return ordinal;
        }

        return TryFormatCardinalNumberedList(text);
    }

    private static string? TryFormatOrdinalNumberedList(string text)
    {
        var matches = OrdinalListMarkerRegex().Matches(text);
        if (matches.Count < 2)
        {
            return null;
        }

        var expected = 1;
        var items = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var number = SpokenOrdinalToInt(matches[i].Value);
            if (number != expected)
            {
                return null;
            }

            var itemStart = matches[i].Index + matches[i].Length;
            var itemEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var item = text[itemStart..itemEnd]
                .Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
            if (item.Length == 0)
            {
                return null;
            }

            item = CapitalizeFirstLetter(item);
            if (i == matches.Count - 1 && !item.EndsWith('.') && !item.EndsWith('!') && !item.EndsWith('?'))
            {
                item += ".";
            }

            items.Add(item);
            expected++;
        }

        if (items.Count < 2)
        {
            return null;
        }

        var intro = "";
        if (matches[0].Index > 0)
        {
            var prefix = text[..matches[0].Index].Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                intro = ApplyBasicSentenceCasing(prefix) + ":\n";
            }
        }

        return intro + string.Join('\n', items.Select((item, index) => $"{index + 1}. {item}"));
    }

    private static string? TryFormatCardinalNumberedList(string text)
    {
        var matches = NumberedListMarkerRegex().Matches(text);
        if (matches.Count < 2 || matches[0].Index != 0)
        {
            return null;
        }

        var expected = 1;
        var items = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var number = SpokenNumberToInt(matches[i].Value);
            if (number != expected)
            {
                return null;
            }

            var itemStart = matches[i].Index + matches[i].Length;
            var itemEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var item = text[itemStart..itemEnd]
                .Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
            if (item.Length == 0)
            {
                return null;
            }

            items.Add(item);
            expected++;
        }

        return items.Count < 2 ? null 
            : string.Join('\n', items.Select((item, index) => $"{index + 1}. {item}"));
    }

    private static string? TryFormatFormalEmail(string text)
    {
        var trimmed = text.Trim();
        var greetingMatch = EmailGreetingRegex().Match(trimmed);
        if (!greetingMatch.Success)
        {
            return null;
        }

        var greeting = CapitalizeFirstLetter(greetingMatch.Groups["greeting"].Value.ToLowerInvariant()) + " "
            + CapitalizeFirstLetter(greetingMatch.Groups["name"].Value) + ",";

        var remainder = trimmed[greetingMatch.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            return greeting;
        }

        string signoff;
        string body;

        var signoffMatch = EmailSignoffRegex().Match(remainder);
        if (signoffMatch.Success)
        {
            var rawSignoff = signoffMatch.Groups["signoff"].Value;
            signoff = CapitalizeWords(rawSignoff) + ".";
            body = remainder[..signoffMatch.Index].Trim(' ', '\t', ',', ';', ':', '-', '\r', '\n');
        }
        else
        {
            signoff = "";
            body = remainder;
        }

        if (body.Length > 0)
        {
            body = ApplyBasicSentenceCasing(body);
            if (!body.EndsWith('.') && !body.EndsWith('!') && !body.EndsWith('?'))
            {
                body += QuestionStartRegex().IsMatch(body) ? "?" : ".";
            }
        }

        if (signoff.Length > 0 && body.Length > 0)
        {
            return $"{greeting}\n\n{body}\n\n{signoff}";
        }

        if (body.Length > 0)
        {
            return $"{greeting}\n\n{body}";
        }

        if (signoff.Length > 0)
        {
            return $"{greeting}\n\n{signoff}";
        }

        return greeting;
    }

    private static string CapitalizeFirstLetter(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string CapitalizeWords(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(CapitalizeFirstLetter));
    }

    private static int SpokenOrdinalToInt(string ordinal)
    {
        return ordinal.ToLowerInvariant() switch
        {
            "first" => 1,
            "second" => 2,
            "third" => 3,
            "fourth" => 4,
            "fifth" => 5,
            "sixth" => 6,
            "seventh" => 7,
            "eighth" => 8,
            "ninth" => 9,
            "tenth" => 10,
            _ => 0,
        };
    }

    private static int SpokenNumberToInt(string number)
    {
        return number.ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            _ => 0,
        };
    }

    private static string ApplyBasicSentenceCasing(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]))
            {
                continue;
            }

            if (char.IsUpper(text[i]))
            {
                return text;
            }

            return text[..i] + char.ToUpperInvariant(text[i]) + text[(i + 1)..];
        }

        return text;
    }

    // Bare er/um are removed only with an English language hint. Elsewhere they
    // can be real words. The evaluator also protects literals and abbreviations.
    [GeneratedRegex(@"(?i)(?<prefix>^|[\s,.;:!?])(?<word>um+|uh+|er+|erm+|ah+|(?<=,\s*)you know(?=\s*,))(?=$|[\s,.;:!?])")]
    private static partial Regex StandaloneFillerRegex();

    [GeneratedRegex("`[^`]*`|\"[^\"]*\"|'[^'\\r\\n]*'")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}_])uh[ \t]+(?:huh|oh|uh)(?![\p{L}\p{N}_])")]
    private static partial Regex AnswerPhraseRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex RepeatedInlineWhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex WhitespaceBeforePunctuationRegex();

    [GeneratedRegex(@",{2,}")]
    private static partial Regex DuplicateCommaRegex();

    [GeneratedRegex(@"^[\s,.;:!?]+")]
    private static partial Regex LeadingNoiseRegex();

    [GeneratedRegex(@"[\s,;:-]+$")]
    private static partial Regex TrailingNoiseRegex();

    [GeneratedRegex(@"(?i)^\s*(?:scratch\s+that|cancel\s+that|never\s+mind)\s*$")]
    private static partial Regex ScratchThatWholeUtteranceRegex();

    [GeneratedRegex(
        @"(?is)(?:^|(?<=[\n.!?]\s*))(?<clause>[^\n.!?]+?)\s*[,;:-]*\s*\b(?<marker>scratch\s+that|no,?\s+wait|never\s+mind|cancel\s+that)\b[\s,.;:!?-]+(?<replacement>[A-Za-z0-9].*)$"
    )]
    private static partial Regex RepairMarkerReplacementRegex();

    [GeneratedRegex(
        @"(?is)\b(?<prefix>(?:.*\s)?)(?:(?<original>[A-Za-z0-9][A-Za-z0-9'-]*)\s+(?<noun>[A-Za-z][A-Za-z'-]*)\s*[,;:-]*\s+(?:actually|i\s+mean)\s*[,;:-]*\s+(?<replacement>[A-Za-z0-9][A-Za-z0-9'-]*\s+\k<noun>)|(?<original>[A-Za-z0-9][A-Za-z0-9'-]*)\s*[,;:-]*\s+(?:actually|i\s+mean)\s*[,;:-]*\s+(?<replacement>[A-Za-z0-9][A-Za-z0-9'-]*))(?<suffix>[.?!]?)$"
    )]
    private static partial Regex OneWordCorrectionRegex();

    [GeneratedRegex(@"(?i)\b(?:first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)\b")]
    private static partial Regex OrdinalListMarkerRegex();

    [GeneratedRegex(@"(?i)\b(?:one|two|three|four|five|six|seven|eight|nine)\b")]
    private static partial Regex NumberedListMarkerRegex();

    [GeneratedRegex(
        @"^(?i:(?<greeting>hi|hello|dear|hey))\s+(?<name>(?:[A-Z][a-z'-]+|[a-z'-]+)(?:\s+[A-Z][a-z'-]+)?)(?:[\s,]+|$)"
    )]
    private static partial Regex EmailGreetingRegex();

    [GeneratedRegex(
        @"(?:[\s,.]+|\b)(?<signoff>thanks|thank\s+you|best\s+regards|warm\s+regards|kind\s+regards|regards|cheers|sincerely|best)[.!]?\s*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex EmailSignoffRegex();

    [GeneratedRegex(
        @"^(?:can|could|would|will|should|do|does|did|is|are|am|was|were|have|has|had|what|where|when|why|how|who|whom|which)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex QuestionStartRegex();

    [GeneratedRegex(
        @"(?i)\b(?<mark>comma|period|full\s+stop|question\s+mark|exclamation\s+(?:mark|point)|colon|semicolon)\b"
    )]
    private static partial Regex SpokenPunctuationRegex();

    [GeneratedRegex(@"(?is)^\s*bullet\s+list\s+(?<items>.+)$")]
    private static partial Regex BulletListTriggerRegex();

    [GeneratedRegex(@"(?i)\s+(?:next\s+bullet|new\s+bullet)\s+")]
    private static partial Regex ExplicitBulletSeparatorRegex();

    [GeneratedRegex(@"\s*[,;]\s*")]
    private static partial Regex BulletPunctuationSeparatorRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z'-]*$")]
    private static partial Regex SingleWordListItemRegex();

    [GeneratedRegex("[A-Za-z][A-Za-z'-]*")]
    private static partial Regex WordRegex();
}
