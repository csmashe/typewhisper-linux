using System;

namespace TypeWhisper.Linux.Cli;

/// <summary>
/// What the parsed argv tells us to do. The driver in <c>Program.Main</c>
/// switches on the <see cref="Kind"/> to decide between launching the GUI,
/// sending a legacy toggle to a running instance, or running a CLI
/// subcommand that prints to stdout and exits.
/// </summary>
internal enum CliActionKind
{
    /// <summary>Launch the GUI (single-instance probe runs separately).</summary>
    LaunchGui,
    /// <summary>Print usage to stdout and exit 0.</summary>
    PrintHelp,
    /// <summary>Bare <c>typewhisper</c> — toggle the running instance, exit 0.</summary>
    BareToggle,
    /// <summary><c>typewhisper record &lt;verb&gt;</c>.</summary>
    Record,
    /// <summary><c>typewhisper status</c>.</summary>
    Status,
    /// <summary>Args didn't parse; the driver should print usage and exit non-zero.</summary>
    Invalid,
}

/// <summary>
/// Result of parsing the command line. The driver does not need a class
/// hierarchy here — there are only a handful of shapes and they all fit in
/// a small bag of optional fields.
/// </summary>
internal sealed record CliAction(
    CliActionKind Kind,
    string? RecordVerb = null,
    string? ErrorMessage = null,
    bool StartMinimized = false);

/// <summary>
/// Translates raw <c>argv</c> into a <see cref="CliAction"/>. Centralizing
/// this keeps <c>Program.Main</c> readable and means the test surface is a
/// pure function — no socket calls, no Avalonia startup.
/// </summary>
internal static class CommandLineParser
{
    /// <summary>
    /// Multi-line usage string printed for <c>--help</c> and on parse
    /// errors. Plain text, no ANSI; the binary may be invoked from
    /// non-terminal contexts (autostart, compositor binds).
    /// </summary>
    public const string UsageText =
        "Usage:\n" +
        "  typewhisper                       Launch the GUI, or toggle dictation if already running.\n" +
        "  typewhisper record start          Start dictation (idempotent).\n" +
        "  typewhisper record stop           Stop dictation and transcribe (idempotent).\n" +
        "  typewhisper record toggle         Start if idle, stop otherwise.\n" +
        "  typewhisper record cancel         Drop in-flight audio with no transcription.\n" +
        "  typewhisper status                Print current state as JSON.\n" +
        "  typewhisper --minimized           Launch the GUI minimized to the tray.\n" +
        "  typewhisper --help                Show this help.\n";

    public static CliAction Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliAction(CliActionKind.BareToggle);

        // --help is checked first so it short-circuits even alongside
        // unrelated flags like --minimized in unusual launch wrappers.
        foreach (var a in args)
        {
            if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase))
            {
                return new CliAction(CliActionKind.PrintHelp);
            }
        }

        // GUI-only flag from Phase 4. If --minimized is present and no
        // subcommand is, we launch the GUI minimized. We treat any extra
        // unknown flags as "GUI launch" rather than rejecting, to avoid
        // breaking forward-compat with autostart entries shipped by older
        // installers.
        var minimized = false;
        var sawNonFlag = false;
        var firstNonFlag = -1;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
            {
                minimized = true;
                continue;
            }
            if (a.StartsWith('-'))
            {
                // Unknown flag — leave it to the GUI startup to ignore.
                continue;
            }
            sawNonFlag = true;
            firstNonFlag = i;
            break;
        }

        if (!sawNonFlag)
        {
            // All args were flags. With no subcommand verb, launch the GUI.
            return new CliAction(CliActionKind.LaunchGui, StartMinimized: minimized);
        }

        // Subcommand starts at args[firstNonFlag]. The CLI grammar only
        // recognizes 'record' and 'status' today; anything else is invalid.
        // Trailing positional arguments past the documented forms are
        // rejected — silently dropping them would let typos like
        // `typewhisper status pls` succeed with surprising semantics.
        var verb = args[firstNonFlag];
        if (string.Equals(verb, "record", StringComparison.OrdinalIgnoreCase))
        {
            if (firstNonFlag + 1 >= args.Length)
                return new CliAction(CliActionKind.Invalid, ErrorMessage: "missing record verb (start|stop|toggle|cancel)");

            var sub = args[firstNonFlag + 1].ToLowerInvariant();
            if (HasUnexpectedTrailingOperand(args, firstNonFlag + 1))
                return new CliAction(CliActionKind.Invalid, ErrorMessage: "unexpected extra arguments after 'record " + sub + "'");

            if (sub is "start" or "stop" or "toggle" or "cancel")
                return new CliAction(CliActionKind.Record, RecordVerb: sub);

            return new CliAction(CliActionKind.Invalid, ErrorMessage: $"unknown record verb '{sub}'");
        }

        if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (HasUnexpectedTrailingOperand(args, firstNonFlag))
                return new CliAction(CliActionKind.Invalid, ErrorMessage: "unexpected extra arguments after 'status'");
            return new CliAction(CliActionKind.Status);
        }

        return new CliAction(CliActionKind.Invalid, ErrorMessage: $"unknown command '{verb}'");
    }

    /// <summary>
    /// Returns true if any argument after index <paramref name="lastConsumedIndex"/>
    /// is a non-flag positional. Flags like <c>--minimized</c> that may
    /// trail a subcommand stay tolerated for forward compatibility with
    /// wrapper scripts, but unknown operands are rejected so typos don't
    /// silently execute the action.
    /// </summary>
    private static bool HasUnexpectedTrailingOperand(string[] args, int lastConsumedIndex)
    {
        for (var i = lastConsumedIndex + 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
                return true;
        }
        return false;
    }
}
