namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
///     Builds ydotool argument vectors. Socket discovery is left to the caller;
///     this class only constructs args for a daemon whose socket is already known.
///     No process is spawned here so the test harness can intercept via the runner.
/// </summary>
internal static class YdotoolBackend
{
    public const string ExecutableName = "ydotool";

    // evdev keycodes that ydotool's `key` verb consumes. ydotool sends
    // raw evdev events through /dev/uinput, so layout-dependent characters
    // (non-US punctuation) can render wrong; the chain falls back to
    // clipboard paste for that case.
    private const int LeftCtrlKey = 29;
    private const int LeftShiftKey = 42;
    private const int CKey = 46;
    private const int VKey = 47;
    private const int EnterKey = 28;

    /// <summary>Returns the env overlay (<c>YDOTOOL_SOCKET</c>) pointing the
    ///     client at the daemon socket; callers merge this into their own env.</summary>
    public static IReadOnlyDictionary<string, string>? BuildEnv(string? socketPath)
    {
        return string.IsNullOrWhiteSpace(socketPath) ? null 
            : new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath };
    }

    public static IReadOnlyList<string> TypeArgs(string text)
    {
        // `--` prevents leading dashes in the text being parsed as flags.
        // Default delays (20/20 ms) give ~40 ms/char — ~8 s for 200 chars.
        // 2/2 ms yields ~250 chars/sec via /dev/uinput; no realistic app
        // drops events and there is still margin for slow VMs.
        return ["type", "--key-delay", "2", "--key-hold", "2", "--", text];
    }

    public static IReadOnlyList<string> PasteArgs()
    {
        // Raw evdev down/up pairs: code:1 = press, code:0 = release.
        return ["key", $"{LeftCtrlKey}:1", $"{VKey}:1", $"{VKey}:0", $"{LeftCtrlKey}:0"];
    }

    public static IReadOnlyList<string> CopyArgs()
    {
        return ["key", $"{LeftCtrlKey}:1", $"{CKey}:1", $"{CKey}:0", $"{LeftCtrlKey}:0"];
    }

    public static IReadOnlyList<string> EnterArgs()
    {
        return ["key", $"{EnterKey}:1", $"{EnterKey}:0"];
    }

    /// <summary>
    ///     Shift+Enter — a non-submitting newline. Chat targets (Slack,
    ///     Discord, web chat, Claude's box) bind Enter to "send" and
    ///     Shift+Enter to "insert newline", so dictated paragraph breaks
    ///     must be typed this way to avoid submitting partial text.
    ///     Press shift, tap Enter, release shift.
    /// </summary>
    public static IReadOnlyList<string> ShiftEnterArgs()
    {
        return ["key", $"{LeftShiftKey}:1", $"{EnterKey}:1", $"{EnterKey}:0", $"{LeftShiftKey}:0"];
    }

    /// <summary>
    ///     No-op probe used by <c>YdotoolSetupHelper</c> to verify the
    ///     client→socket→daemon→/dev/uinput path. Releasing Left Alt (code 56)
    ///     has no visible effect but still requires a successful evdev write —
    ///     which fails with EACCES when the user lacks uinput access.
    /// </summary>
    public static IReadOnlyList<string> ProbeArgs()
    {
        return ["key", "56:0"];
    }
}