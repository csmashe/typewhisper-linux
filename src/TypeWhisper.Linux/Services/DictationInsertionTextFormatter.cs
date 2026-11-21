using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Formats the final dictated text immediately before it is injected into the
///     target application. Only the inserted text changes — history, recent
///     transcriptions, and completion events keep the final text.
/// </summary>
internal static class DictationInsertionTextFormatter
{
    /// <summary>
    ///     With the text around the cursor, fits the dictation to it (<see cref="DictationInsertionText.ForPaste" />).
    ///     Without it, appends a single trailing space so consecutive dictations don't run together:
    ///     unlike upstream, which then pastes unchanged, because many Linux targets expose no readable text.
    /// </summary>
    public static string TextForInsertion(
        string text,
        DictationInsertionContext? context = null,
        bool stripFinalPeriodFromStandaloneValues = true,
        string? language = null)
    {
        if (context is null)
        {
            return PadEnd(text);
        }

        var fitted = DictationInsertionText.ForPaste(text, context, stripFinalPeriodFromStandaloneValues, language);
        // Only spaces trim to nothing; insert them as dictated.
        if (fitted.Length == 0)
        {
            return text;
        }

        // At the end of a field, keep the trailing space so a next dictation whose context can't be read
        // still doesn't run into this one; only a standalone value that lost its period stays exact.
        if (context.After.Length > 0)
        {
            return fitted;
        }

        var strippedStandaloneValue = context.Before.Length == 0 && stripFinalPeriodFromStandaloneValues
            && fitted != DictationInsertionText.ForPaste(text, context, false, language);
        return strippedStandaloneValue ? fitted : PadEnd(fitted);
    }

    private static string PadEnd(string text)
    {
        if (string.IsNullOrEmpty(text) || char.IsWhiteSpace(text[^1]))
        {
            return text;
        }

        return text + " ";
    }
}
