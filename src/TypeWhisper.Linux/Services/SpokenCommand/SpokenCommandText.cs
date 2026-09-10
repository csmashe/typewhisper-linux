namespace TypeWhisper.Linux.Services.SpokenCommand;

/// <summary>
///     Shared text helpers for spoken-command parsing: the whitespace/alphanumeric tokenizer and the
///     leading politeness-filler set. Kept in one place so the intent classifier
///     (<see cref="SpokenCommandIntent" />) and the action matcher
///     (<see cref="SpokenCommandActionMatcher" />) can't drift apart.
/// </summary>
internal static class SpokenCommandText
{
    // Politeness/filler that can precede the real verb or action name ("please write…",
    // "can you email…"); skipped when locating the first meaningful command token so the filler
    // doesn't hide the leading verb (SpokenCommandIntent) or a single-word action name
    // (SpokenCommandActionMatcher).
    public static readonly IReadOnlySet<string> LeadingFillers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "please", "pls", "kindly", "just", "can", "could", "would", "you",
        };

    // Splits on whitespace and keeps only alphanumerics per token, dropping empties. Casing is
    // preserved; callers compare case-insensitively (or normalize further) as they need.
    public static IEnumerable<string> Tokenize(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 0);
    }
}
