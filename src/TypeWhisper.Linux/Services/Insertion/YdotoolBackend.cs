using System.Collections.Generic;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
/// Wire-level invocations for the <c>ydotool</c> client. Socket discovery
/// lives on <see cref="SystemCommandAvailabilityService"/> (so it can run
/// at snapshot-build time without us re-implementing it here); this class
/// only knows how to talk to a daemon whose socket is already located.
///
/// The argument vectors are stable enough that the calling platform can
/// hand them straight to its process runner — we deliberately don't
/// allocate a process here so the existing test harness (which intercepts
/// runner.Run) keeps working.
/// </summary>
internal static class YdotoolBackend
{
    public const string ExecutableName = "ydotool";

    // evdev keycodes that ydotool's `key` verb consumes. ydotool sends
    // raw evdev events through /dev/uinput, so layout-dependent characters
    // (non-US punctuation) can render wrong; the chain falls back to
    // clipboard paste for that case.
    private const int LeftCtrlKey = 29;
    private const int CKey = 46;
    private const int VKey = 47;
    private const int EnterKey = 28;

    /// <summary>
    /// Build the environment overlay that points the ydotool client at
    /// the discovered daemon socket. The client also reads
    /// <c>YDOTOOL_SOCKET</c> from its own environment, so callers should
    /// merge this on top of their own env.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? BuildEnv(string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            return null;
        return new Dictionary<string, string>
        {
            ["YDOTOOL_SOCKET"] = socketPath,
        };
    }

    public static IReadOnlyList<string> TypeArgs(string text) =>
        // ydotool's `type` verb writes a string the daemon synthesizes as
        // key presses. We prefix with `--` so leading dashes in the text
        // aren't parsed as flags.
        //
        // The default --key-delay=20 --key-hold=20 means ~40 ms per
        // character — visibly slow on multi-sentence dictations (a 200
        // char block takes 8 s). xdotool's path uses --delay 8 for the
        // same job; ydotool can go lower because it dispatches through
        // /dev/uinput rather than a layout-translating tool. 2 / 2 gives
        // ~250 chars/sec, fast enough that no realistic app drops events
        // on modern hardware and still leaves margin for slow VMs.
        new[] { "type", "--key-delay", "2", "--key-hold", "2", "--", text };

    public static IReadOnlyList<string> PasteArgs() =>
        // Ctrl+V emitted as raw evdev down/up pairs. ydotool's `key`
        // verb takes `<code>:<value>` tuples — value 1 = press, 0 = release.
        new[]
        {
            "key",
            $"{LeftCtrlKey}:1",
            $"{VKey}:1",
            $"{VKey}:0",
            $"{LeftCtrlKey}:0",
        };

    public static IReadOnlyList<string> CopyArgs() =>
        new[]
        {
            "key",
            $"{LeftCtrlKey}:1",
            $"{CKey}:1",
            $"{CKey}:0",
            $"{LeftCtrlKey}:0",
        };

    public static IReadOnlyList<string> EnterArgs() =>
        new[] { "key", $"{EnterKey}:1", $"{EnterKey}:0" };

    /// <summary>
    /// No-op key release used by <c>YdotoolSetupHelper</c> to prove the
    /// full client → socket → daemon → /dev/uinput pipe is usable.
    /// Releasing an unpressed key (Left Alt, code 56) generates no
    /// visible effect in any window but still requires the daemon to
    /// successfully write an evdev event — which is exactly what fails
    /// with EACCES when the user lacks uinput access.
    /// </summary>
    public static IReadOnlyList<string> ProbeArgs() =>
        new[] { "key", "56:0" };
}
