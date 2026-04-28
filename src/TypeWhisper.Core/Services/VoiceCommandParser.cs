using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

public sealed record VoiceCommandParseResult(
    string Text,
    bool AutoEnter = false,
    bool CancelInsertion = false);

public sealed class VoiceCommandParser
{
    private static readonly Regex PressEnterSuffix = BuildSuffixRegex("press enter");
    private static readonly Regex NewParagraphSuffix = BuildSuffixRegex("new paragraph");
    private static readonly Regex NewLineSuffix = BuildSuffixRegex("new line");
    private static readonly Regex CancelSuffix = BuildSuffixRegex("cancel");
    private static readonly Regex TrailingNoise = new(@"[\s,.;:!?]+$", RegexOptions.Compiled);

    public VoiceCommandParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new VoiceCommandParseResult(text);

        var current = text.Trim();
        var autoEnter = false;

        while (true)
        {
            if (TryRemoveSuffix(current, CancelSuffix, out var withoutCancel))
            {
                var remaining = TrimTrailingNoise(withoutCancel);
                if (string.IsNullOrWhiteSpace(remaining) || string.IsNullOrWhiteSpace(Parse(remaining).Text))
                    return new VoiceCommandParseResult("", CancelInsertion: true);

                current = remaining;
                continue;
            }

            if (TryRemoveSuffix(current, PressEnterSuffix, out var withoutEnter))
            {
                autoEnter = true;
                current = TrimTrailingNoise(withoutEnter);
                continue;
            }

            if (TryRemoveSuffix(current, NewParagraphSuffix, out var withoutParagraph))
            {
                current = TrimTrailingNoise(withoutParagraph) + "\n\n";
                continue;
            }

            if (TryRemoveSuffix(current, NewLineSuffix, out var withoutLine))
            {
                current = TrimTrailingNoise(withoutLine) + "\n";
                continue;
            }

            break;
        }

        return new VoiceCommandParseResult(current, autoEnter);
    }

    private static Regex BuildSuffixRegex(string phrase)
    {
        var escaped = Regex.Escape(phrase).Replace(@"\ ", @"\s+");
        return new Regex($@"(?:^|\s){escaped}[\s,.;:!?]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static bool TryRemoveSuffix(string text, Regex suffix, out string result)
    {
        if (!suffix.IsMatch(text))
        {
            result = text;
            return false;
        }

        result = suffix.Replace(text, "");
        return true;
    }

    private static string TrimTrailingNoise(string text) => TrailingNoise.Replace(text, "");
}
