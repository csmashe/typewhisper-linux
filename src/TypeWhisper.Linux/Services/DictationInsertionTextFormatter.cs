namespace TypeWhisper.Linux.Services;

/// <summary>
///     Formats the final dictated text immediately before it is injected into the
///     target application. Appends a single trailing space (when the text does not
///     already end in whitespace) so that consecutive dictations do not run
///     together into a single word. The space is applied only to the inserted
///     text — history, recent transcriptions, and completion events keep the
///     unpadded final text.
/// </summary>
internal static class DictationInsertionTextFormatter
{
    public static string TextForInsertion(string text)
    {
        if (string.IsNullOrEmpty(text) || char.IsWhiteSpace(text[^1]))
            return text;

        return text + " ";
    }
}
