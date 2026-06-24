using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Strips recognized trailing spoken commands ("press enter", "new line", "new paragraph",
///     "cancel") from transcribed text, including stacked commands, and reports the resulting
///     <see cref="VoiceCommandParseResult" />.
/// </summary>
public sealed class VoiceCommandParser
{
    private static readonly Regex s_pressEnterSuffix = BuildSuffixRegex("press enter");
    private static readonly Regex s_newParagraphSuffix = BuildSuffixRegex("new paragraph");
    private static readonly Regex s_newLineSuffix = BuildSuffixRegex("new line");
    private static readonly Regex s_cancelSuffix = BuildSuffixRegex("cancel");
    private static readonly Regex s_trailingNoise = new(@"[\s,.;:!?]+$", RegexOptions.Compiled);

    public static VoiceCommandParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new VoiceCommandParseResult(text);
        }

        var current = text.Trim();
        var trailingOutput = new StringBuilder();
        var autoEnter = false;

        // Repeatedly strip recognized trailing commands so stacked commands like
        // "hello new line press enter" are handled correctly.
        while (true)
        {
            if (TryRemoveSuffix(current, s_cancelSuffix, out var withoutCancel))
            {
                var remaining = TrimTrailingNoise(withoutCancel);
                if (
                    string.IsNullOrWhiteSpace(remaining)
                    || string.IsNullOrWhiteSpace(Parse(remaining).Text)
                )
                {
                    return new VoiceCommandParseResult("", CancelInsertion: true);
                }

                current = remaining;
                continue;
            }

            if (TryRemoveSuffix(current, s_pressEnterSuffix, out var withoutEnter))
            {
                autoEnter = true;
                current = TrimTrailingNoise(withoutEnter);
                continue;
            }

            if (TryRemoveSuffix(current, s_newParagraphSuffix, out var withoutParagraph))
            {
                current = TrimTrailingNoise(withoutParagraph);
                trailingOutput.Insert(0, "\n\n");
                continue;
            }

            if (TryRemoveSuffix(current, s_newLineSuffix, out var withoutLine))
            {
                current = TrimTrailingNoise(withoutLine);
                trailingOutput.Insert(0, "\n");
                continue;
            }

            break;
        }

        return new VoiceCommandParseResult(current + trailingOutput, autoEnter);
    }

    private static Regex BuildSuffixRegex(string phrase)
    {
        var escaped = Regex.Escape(phrase).Replace(@"\ ", @"\s+");
        return new Regex(
            $@"(?:^|\s){escaped}[\s,.;:!?]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
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

    private static string TrimTrailingNoise(string text)
    {
        return s_trailingNoise.Replace(text, "");
    }
}