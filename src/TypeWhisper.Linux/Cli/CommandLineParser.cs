namespace TypeWhisper.Linux.Cli;

/// <summary>What the parsed argv tells us to do; Program.Main switches on <see cref="Kind" />.</summary>
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
    Invalid
}

/// <summary>Result of parsing the command line.</summary>
internal sealed record CliAction(
    CliActionKind Kind,
    string? RecordVerb = null,
    string? ErrorMessage = null,
    bool StartMinimized = false
);

/// <summary>
///     Translates raw argv into a <see cref="CliAction" />. Pure function —
///     no socket calls or Avalonia startup — so the parse path is fully testable.
/// </summary>
internal static class CommandLineParser
{
    /// <summary>Usage string for --help and parse errors. Plain text (no ANSI) — may run from non-terminal contexts.</summary>
    public const string UsageText =
        "Usage:\n"
        + "  typewhisper                       Launch the GUI, or toggle dictation if already running.\n"
        + "  typewhisper record start          Start dictation (idempotent).\n"
        + "  typewhisper record stop           Stop dictation and transcribe (idempotent).\n"
        + "  typewhisper record toggle         Start if idle, stop otherwise.\n"
        + "  typewhisper record cancel         Drop in-flight audio with no transcription.\n"
        + "  typewhisper status                Print current state as JSON.\n"
        + "  typewhisper --minimized           Launch the GUI minimized to the tray.\n"
        + "  typewhisper --help                Show this help.\n";

    public static CliAction Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliAction(CliActionKind.BareToggle);
        }

        // --help short-circuits even alongside other flags like --minimized.
        foreach (var a in args)
        {
            if (
                string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)
            )
            {
                return new CliAction(CliActionKind.PrintHelp);
            }
        }

        // --minimized with no subcommand launches the GUI minimized. Unknown flags
        // are treated as GUI launch to preserve forward-compat with older autostart entries.
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
            return new CliAction(CliActionKind.LaunchGui, StartMinimized: minimized);
        }

        // Only 'record' and 'status' are recognized; trailing positional arguments
        // are rejected so typos like `typewhisper status pls` don't silently succeed.
        var verb = args[firstNonFlag];
        if (string.Equals(verb, "record", StringComparison.OrdinalIgnoreCase))
        {
            if (firstNonFlag + 1 >= args.Length)
            {
                return new CliAction(
                    CliActionKind.Invalid,
                    ErrorMessage: "missing record verb (start|stop|toggle|cancel)"
                );
            }

            var sub = args[firstNonFlag + 1].ToLowerInvariant();
            if (HasUnexpectedTrailingOperand(args, firstNonFlag + 1))
            {
                return new CliAction(
                    CliActionKind.Invalid,
                    ErrorMessage: "unexpected extra arguments after 'record " + sub + "'"
                );
            }

            if (sub is "start" or "stop" or "toggle" or "cancel")
            {
                return new CliAction(CliActionKind.Record, sub);
            }

            return new CliAction(
                CliActionKind.Invalid,
                ErrorMessage: $"unknown record verb '{sub}'"
            );
        }

        if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (HasUnexpectedTrailingOperand(args, firstNonFlag))
            {
                return new CliAction(
                    CliActionKind.Invalid,
                    ErrorMessage: "unexpected extra arguments after 'status'"
                );
            }

            return new CliAction(CliActionKind.Status);
        }

        return new CliAction(CliActionKind.Invalid, ErrorMessage: $"unknown command '{verb}'");
    }

    /// <summary>
    ///     Returns true if any argument after <paramref name="lastConsumedIndex" />
    ///     is a non-flag positional. Trailing flags are tolerated for forward compat;
    ///     unknown operands are rejected so typos don't silently execute the action.
    /// </summary>
    private static bool HasUnexpectedTrailingOperand(string[] args, int lastConsumedIndex)
    {
        for (var i = lastConsumedIndex + 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                return true;
            }
        }

        return false;
    }
}