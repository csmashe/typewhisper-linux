using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>Applies full local fallback rules, limiting spacing changes to matched command boundaries.</summary>
public sealed class SpokenFormattingService
{
    private readonly SpokenFormattingRulesLoader _rulesLoader;
    private readonly ConcurrentDictionary<string, SpokenFormattingRule[]> _orderedRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    /// <summary>Initializes fallback formatting with the supplied language rules.</summary>
    public SpokenFormattingService(SpokenFormattingRulesLoader rulesLoader)
    {
        _rulesLoader = rulesLoader;
    }

    /// <summary>Replaces spoken commands for the language, preserving unmatched text and unsupported-language input.</summary>
    public string Normalize(
        string text,
        string? language)
    {
        if (string.IsNullOrEmpty(text) || _rulesLoader.RuleSetFor(language) is not { } ruleSet)
            return text;

        return OrderedRules(ruleSet).Aggregate(text, ReplaceRule);
    }

    private SpokenFormattingRule[] OrderedRules(SpokenFormattingRuleSet ruleSet) =>
        _orderedRules.GetOrAdd(ruleSet.Language, _ =>
        [
            .. ruleSet.Rules
                .Where(static rule => rule.Category == SpokenFormattingRuleCategory.Structural)
                .OrderByDescending(static rule => rule.Phrase.Length),
            .. ruleSet.Rules
                .Where(static rule => rule.Category != SpokenFormattingRuleCategory.Structural)
                .OrderByDescending(static rule => rule.Phrase.Length),
        ]);

    private string ReplaceRule(string text, SpokenFormattingRule rule)
    {
        var regex = _patterns.GetOrAdd(
            $"{rule.Category}:{rule.Phrase}",
            _ => BuildPattern(rule));
        StringBuilder? result = null;
        var end = 0;
        var producedTrailingSpace = false;
        foreach (Match match in regex.Matches(text))
        {
            result ??= new StringBuilder(text.Length);
            // An adjacent match owns the space emitted by the previous replacement.
            var inheritedLead = producedTrailingSpace && match.Index == end;
            if (inheritedLead)
                result.Length--;
            result.Append(text, end, match.Index - end);
            end = match.Index + match.Length;
            var lead = inheritedLead || match.Groups["lead"].Length > 0;
            var trail = match.Groups["trail"].Length > 0;
            producedTrailingSpace = false;

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- Early exits for structural and punctuation rules keep shared placement handling readable.
            if (rule.Category == SpokenFormattingRuleCategory.Structural)
            {
                result.Append(rule.Replacement);
                continue;
            }

            if (rule.Category == SpokenFormattingRuleCategory.Punctuation)
            {
                if (IsDuplicatePunctuation(NextNonWhitespace(text, end), rule.Replacement))
                    continue;

                var duplicate = IsDuplicatePunctuation(PreviousNonWhitespace(result), rule.Replacement);
                if (!duplicate)
                    result.Append(rule.Replacement);
                producedTrailingSpace = trail
                    && (duplicate || end < text.Length && text[end] is not '\r' and not '\n')
                    && !ClosesSegment(NextNonWhitespace(text, end));
            }
            else
            {
                if (rule.Placement == SpokenFormattingRulePlacement.Open && lead
                    && result.Length > 0 && !char.IsWhiteSpace(result[^1])
                    && result[^1] is not ('(' or '[' or '{' or '“' or '„' or '«' or '‹' or '"' or '\''))
                    result.Append(' ');
                result.Append(rule.Replacement);
                producedTrailingSpace = rule.Placement == SpokenFormattingRulePlacement.Close && trail
                    && !ClosesSegment(NextNonWhitespace(text, end));
            }

            if (producedTrailingSpace)
                result.Append(' ');
        }

        return result is null ? text : result.Append(text, end, text.Length - end).ToString();
    }

    private static Regex BuildPattern(SpokenFormattingRule rule)
    {
        var escapedWords = rule.Phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape);
        // Words of a phrase are joined by spaces only: a tab or line break between them was
        // produced by an earlier structural command and ends the phrase.
        var phrasePattern = string.Join("[ ]+", escapedWords);
        var boundedPhrase = $@"(?<![\p{{L}}\p{{M}}\p{{N}}]){phrasePattern}(?![\p{{L}}\p{{M}}\p{{N}}])";
        const string attachedPunctuation = @"[\.,:;?!]";
        var pairedAsrCommas = $@",[ \f\v]+{boundedPhrase},";
        var pattern = rule.Category == SpokenFormattingRuleCategory.Structural
            ? $"(?:{pairedAsrCommas}|{boundedPhrase}{attachedPunctuation}?)"
            : boundedPhrase;
        // Speech contributes spaces; tabs and line breaks are content or structural output and must survive.
        // ReSharper disable once SYSLIB1045 -- The pattern is composed at runtime per rule; GeneratedRegex requires a compile-time constant.
        return new Regex(
            $"(?<lead>[ ]*){pattern}(?<trail>[ ]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    // Punctuation or a closing delimiter right after a replacement: no space is inserted before it.
    private static bool ClosesSegment(char? character) =>
        character is '.' or ',' or ':' or ';' or '?' or '!'
            or ')' or ']' or '}' or '”' or '“' or '»' or '›' or '"' or '\'';

    private static bool IsDuplicatePunctuation(char? character, string replacement) =>
        replacement.Length == 1 && character is not null
        && CanonicalPunctuation(character.Value) == CanonicalPunctuation(replacement[0]);

    // Only spaces are skipped: a tab or line break is a structural boundary, and a mark on the
    // other side of it is a separate dictated mark, not a duplicate.
    private static char? PreviousNonWhitespace(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] != ' ')
                return text[i];
        }

        return null;
    }

    private static char? NextNonWhitespace(string text, int index)
    {
        for (var i = index; i < text.Length; i++)
        {
            if (text[i] != ' ')
                return text[i];
        }

        return null;
    }

    private static char CanonicalPunctuation(char character) => character switch
    {
        '。' => '.',
        '、' => ',',
        '？' => '?',
        '！' => '!',
        '：' => ':',
        '；' => ';',
        _ => character,
    };

}
