namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Manages the sentinel-comment block written into Hyprland/Sway compositor configs,
///     enabling in-place updates and clean removal without duplicating lines.
///     Format (identical across compositors):
///     <code>
/// # >>> typewhisper:dictation (managed; do not edit between sentinels)
/// ...managed lines...
/// # &lt;&lt;&lt; typewhisper:dictation
/// </code>
///     Either zero or exactly one matched pair is acceptable. Any other state
///     (stray open, two opens, etc.) is treated as mismatched and we refuse to touch the file.
/// </summary>
public static class SentinelBlock
{
    public const string OpenSentinel =
        "# >>> typewhisper:dictation (managed; do not edit between sentinels)";

    public const string CloseSentinel = "# <<< typewhisper:dictation";
    private const string OpenPrefix = "# >>> typewhisper:dictation";

    /// <summary>
    ///     Find the managed block. Returns line numbers (zero-based) and a
    ///     <see cref="SentinelScan.Mismatched" /> flag set when the file
    ///     contains an inconsistent set of sentinel comments. Reason is
    ///     populated when mismatched so the UI can surface what's wrong.
    /// </summary>
    public static SentinelScan Scan(string contents)
    {
        var lines = SplitLines(contents);
        var opens = new List<int>();
        var closes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimEnd();
            // Anchor on the prefix so users can strip the "(managed; ...)" annotation.
            if (t.StartsWith(OpenPrefix, StringComparison.Ordinal))
            {
                opens.Add(i);
            }
            else if (t == CloseSentinel)
            {
                closes.Add(i);
            }
        }

        switch (opens.Count, closes.Count)
        {
            case (0, 0):
                return new SentinelScan(false, null, null, null);
            case (1, 1) when opens[0] < closes[0]:
                return new SentinelScan(false, opens[0], closes[0], null);
        }

        var reason = $"Found {opens.Count} open sentinel(s) and {closes.Count} close sentinel(s).";
        return new SentinelScan(
            true,
            opens.Count > 0 ? opens[0] : null,
            closes.Count > 0 ? closes[0] : null,
            reason
        );
    }

    /// <summary>
    ///     Replaces the managed block, or appends a new one if none exists.
    ///     Assumes either zero or one well-formed block; throws if sentinels are mismatched.
    /// </summary>
    public static string ReplaceOrAppend(string contents, IEnumerable<string> managedLines)
    {
        var scan = Scan(contents);
        if (scan.Mismatched)
        {
            throw new InvalidOperationException(
                "Refusing to replace mismatched managed block: " + scan.Reason
            );
        }

        var lines = SplitLines(contents);
        var block = new List<string> { OpenSentinel };
        block.AddRange(managedLines);
        block.Add(CloseSentinel);

        if (scan is { OpenLine: { } open, CloseLine: { } close })
        {
            // Replace [open..close] inclusive, preserving the rest of the file's ordering.
            var prefix = lines.Take(open).ToList();
            var suffix = lines.Skip(close + 1).ToList();
            prefix.AddRange(block);
            prefix.AddRange(suffix);
            return JoinLines(prefix, contents);
        }

        // No block present — append. Avoid doubling the trailing newline before the sentinel.
        var appended = new List<string>(lines);
        if (appended.Count > 0 && !string.IsNullOrEmpty(appended[^1]))
        {
            appended.Add(string.Empty);
        }

        appended.AddRange(block);
        return JoinLines(appended, contents);
    }

    /// <summary>
    ///     Removes the managed block entirely. Throws on mismatched sentinels,
    ///     same rule as <see cref="ReplaceOrAppend" />.
    /// </summary>
    public static string Remove(string contents)
    {
        var scan = Scan(contents);
        if (scan.Mismatched)
        {
            throw new InvalidOperationException(
                "Refusing to remove mismatched managed block: " + scan.Reason
            );
        }

        if (scan.OpenLine is null)
        {
            return contents;
        }

        var lines = SplitLines(contents);
        var open = scan.OpenLine.Value;
        var close = scan.CloseLine!.Value;
        var prefix = lines.Take(open).ToList();
        // Trim one trailing blank line so removal doesn't leave spurious empty lines.
        if (prefix.Count > 0 && string.IsNullOrWhiteSpace(prefix[^1]))
        {
            prefix.RemoveAt(prefix.Count - 1);
        }

        prefix.AddRange(lines.Skip(close + 1));
        return JoinLines(prefix, contents);
    }

    /// <summary>
    ///     Returns the managed lines between the sentinels (exclusive, trailing-whitespace trimmed),
    ///     or null if there is no well-formed block. Used to verify an installed block matches the
    ///     current spec — sentinel presence alone doesn't prove the trigger/command is current.
    /// </summary>
    public static List<string>? ExtractBlockLines(string contents)
    {
        var scan = Scan(contents);
        if (scan.Mismatched || scan.OpenLine is not { } open || scan.CloseLine is not { } close)
        {
            return null;
        }

        var lines = SplitLines(contents);
        var inner = new List<string>();
        for (var i = open + 1; i < close; i++)
        {
            inner.Add(lines[i].TrimEnd());
        }

        return inner;
    }

    private static List<string> SplitLines(string contents)
    {
        // Normalise CRLF to LF for splitting; JoinLines restores the original line ending.
        return contents.Length == 0 ? [] : contents.Replace("\r\n", "\n").Split('\n').ToList();
    }

    private static string JoinLines(List<string> lines, string original)
    {
        var sep = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var joined = string.Join(sep, lines);

        // Preserve the original trailing-newline state. The append path consumes the
        // trailing empty element as a separator signal and would otherwise drop it.
        var originalEndsWithNewline = original.EndsWith('\n');
        var joinedEndsWithNewline = joined.EndsWith(sep, StringComparison.Ordinal);
        switch (originalEndsWithNewline, joinedEndsWithNewline)
        {
            case (true, false):
                joined += sep;
                break;
            case (false, true):
                // Slice exactly one separator — TrimEnd would over-trim files with multiple trailing blanks.
                joined = joined[..^sep.Length];
                break;
        }

        return joined;
    }

    /// <summary>Result of analyzing an existing config file.</summary>
    public sealed record SentinelScan(
        bool Mismatched,
        int? OpenLine,
        int? CloseLine,
        string? Reason
    );
}