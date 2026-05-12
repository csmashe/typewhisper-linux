namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// Tiny utility shared by the Hyprland and Sway writers for managing
/// the sentinel-comment block we write into the user's compositor
/// config. The block lets us update-in-place and cleanly remove on a
/// subsequent run without risk of duplicating lines.
///
/// The format is identical across compositors:
///
/// <code>
/// # >>> typewhisper:dictation (managed; do not edit between sentinels)
/// ...managed lines...
/// # &lt;&lt;&lt; typewhisper:dictation
/// </code>
///
/// Either zero occurrences (fresh install) or exactly one matched
/// pair is acceptable. Anything else — a stray open without close,
/// two opens, etc. — is treated as "mismatched" and we refuse to
/// touch the file, surfacing the situation to the user with an
/// actionable error message.
/// </summary>
public static class SentinelBlock
{
    public const string OpenSentinel  = "# >>> typewhisper:dictation (managed; do not edit between sentinels)";
    public const string CloseSentinel = "# <<< typewhisper:dictation";
    private const string OpenPrefix   = "# >>> typewhisper:dictation";

    /// <summary>Result of analyzing an existing config file.</summary>
    public sealed record SentinelScan(bool Mismatched, int? OpenLine, int? CloseLine, string? Reason);

    /// <summary>
    /// Find the managed block. Returns line numbers (zero-based) and a
    /// <see cref="SentinelScan.Mismatched"/> flag set when the file
    /// contains an inconsistent set of sentinel comments. Reason is
    /// populated when mismatched so the UI can surface what's wrong.
    /// </summary>
    public static SentinelScan Scan(string contents)
    {
        var lines = SplitLines(contents);
        var opens = new List<int>();
        var closes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimEnd();
            // Tolerate users renaming our annotation suffix (e.g.
            // stripping the "(managed; ...)" comment) — what we
            // actually anchor on is the prefix "# >>> typewhisper:dictation".
            if (t.StartsWith(OpenPrefix, StringComparison.Ordinal)) opens.Add(i);
            else if (t == CloseSentinel) closes.Add(i);
        }

        if (opens.Count == 0 && closes.Count == 0)
            return new SentinelScan(false, null, null, null);
        if (opens.Count == 1 && closes.Count == 1 && opens[0] < closes[0])
            return new SentinelScan(false, opens[0], closes[0], null);

        var reason = $"Found {opens.Count} open sentinel(s) and {closes.Count} close sentinel(s).";
        return new SentinelScan(true, opens.Count > 0 ? opens[0] : null,
                                       closes.Count > 0 ? closes[0] : null, reason);
    }

    /// <summary>
    /// Replace the managed block (or append a new one at end-of-file
    /// when none exists) with the supplied lines. The caller is
    /// responsible for refusing to call this when <see cref="Scan"/>
    /// reports mismatched sentinels; this helper assumes either zero
    /// or one well-formed block.
    /// </summary>
    public static string ReplaceOrAppend(string contents, IEnumerable<string> managedLines)
    {
        var scan = Scan(contents);
        if (scan.Mismatched)
            throw new InvalidOperationException("Refusing to replace mismatched managed block: " + scan.Reason);

        var lines = SplitLines(contents);
        var block = new List<string> { OpenSentinel };
        block.AddRange(managedLines);
        block.Add(CloseSentinel);

        if (scan.OpenLine is int open && scan.CloseLine is int close)
        {
            // Remove [open..close] inclusive and insert the new block
            // in the same position so the rest of the file keeps its
            // line ordering.
            var prefix = lines.Take(open).ToList();
            var suffix = lines.Skip(close + 1).ToList();
            prefix.AddRange(block);
            prefix.AddRange(suffix);
            return JoinLines(prefix, contents);
        }

        // No block present — append. Make sure we don't double-up the
        // trailing newline before the sentinel.
        var appended = new List<string>(lines);
        if (appended.Count > 0 && !string.IsNullOrEmpty(appended[^1]))
            appended.Add(string.Empty);
        appended.AddRange(block);
        return JoinLines(appended, contents);
    }

    /// <summary>
    /// Strip the managed block entirely, returning the remaining
    /// contents. Throws when sentinels are mismatched, by the same
    /// rule as <see cref="ReplaceOrAppend"/>.
    /// </summary>
    public static string Remove(string contents)
    {
        var scan = Scan(contents);
        if (scan.Mismatched)
            throw new InvalidOperationException("Refusing to remove mismatched managed block: " + scan.Reason);
        if (scan.OpenLine is null) return contents;

        var lines = SplitLines(contents);
        var open = scan.OpenLine.Value;
        var close = scan.CloseLine!.Value;
        var prefix = lines.Take(open).ToList();
        // Trim a single trailing blank line before the block — keeps
        // round-trip removal from leaving spurious empty lines behind.
        if (prefix.Count > 0 && string.IsNullOrWhiteSpace(prefix[^1])) prefix.RemoveAt(prefix.Count - 1);
        prefix.AddRange(lines.Skip(close + 1));
        return JoinLines(prefix, contents);
    }

    private static List<string> SplitLines(string contents)
    {
        if (contents.Length == 0) return new List<string>();
        // Preserve any choice between "\n" and "\r\n" — we don't get
        // many CRLF compositor configs in the wild but it's a cheap
        // safety net.
        return contents.Replace("\r\n", "\n").Split('\n').ToList();
    }

    private static string JoinLines(List<string> lines, string original)
    {
        var sep = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(sep, lines);
    }
}
