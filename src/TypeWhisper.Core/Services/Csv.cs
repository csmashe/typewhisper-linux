namespace TypeWhisper.Core.Services;

/// <summary>
///     Shared RFC 4180 field escaping for the dictionary and history CSV exporters,
///     so the two exporters can't drift in how they quote values.
/// </summary>
internal static class Csv
{
    /// <summary>
    ///     Quotes a field only when it contains a comma, double-quote, CR or LF,
    ///     doubling any embedded double-quotes per RFC 4180.
    /// </summary>
    public static string Escape(string value)
    {
        if (
            !value.Contains(',')
            && !value.Contains('"')
            && !value.Contains('\n')
            && !value.Contains('\r')
        )
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
