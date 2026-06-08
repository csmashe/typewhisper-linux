using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures the dictation hotkey fires globally AND supports the full
///     tap-vs-hold behavior (tap toggles, hold = push-to-talk). The mechanism
///     is the app's own in-process backend, not a desktop shortcut:
///     <list type="bullet">
///         <item>
///             On an X11 session the in-app hook is already global — nothing
///             to do.
///         </item>
///         <item>
///             On a Wayland session the compositor won't deliver global keys to
///             the app, so it reads input devices directly via evdev — which
///             requires the user to be in the <c>input</c> group. We add them
///             (one admin prompt) and they log out/in once to activate it.
///         </item>
///     </list>
///     We deliberately do NOT use a desktop "custom shortcut" (gsettings / KDE /
///     compositor bind): those only deliver a single activation pulse per press
///     (no key-release), so they can't do hold-to-talk, and a held key
///     autorepeats them into a rapid start/stop thrash. evdev gives real
///     press/release events, which is what the hold modes need.
/// </summary>
public sealed class GlobalHotkeySetupTask : ISetupTask
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly IProcessRunner _runner;

    public GlobalHotkeySetupTask(SystemCommandAvailabilityService commands, IProcessRunner runner)
    {
        _commands = commands;
        _runner = runner;
    }

    public string Id => "global-hotkey";
    public string Title => "Global dictation shortcut";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Required;

    public bool AppliesToThisMachine() => true;

    private bool IsWayland => _commands.GetSnapshot().SessionType == "Wayland";

    private static string CurrentUser => Environment.UserName;

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        // X11: the in-app hook captures global keys (with press/release) without
        // any group membership — tap and hold both work already.
        if (!IsWayland)
        {
            return Satisfied("Global shortcut active. Tap toggles; hold for push-to-talk.");
        }

        // Wayland: evdev (and therefore the global hotkey) needs the input group.
        if (InputGroupCheck.CurrentUserInInputGroup() == true)
        {
            return Satisfied(
                "Global shortcut active via evdev. Tap toggles; hold for push-to-talk."
            );
        }

        // Added to /etc/group but not yet effective in this session: the only
        // thing left is the re-login. Treat as satisfied-with-caveat (like a
        // KDE shortcut that needs a session restart) so Finish isn't blocked —
        // the wizard's final step reminds the user to log out.
        if (UserListedInInputGroupFile())
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.Satisfied,
                    "Added to the input group — log out and back in to activate the shortcut."
                )
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                "Global shortcut needs the input group.",
                "On Wayland the hotkey reads input devices directly (so it can do "
                + "hold-to-talk), which requires your user to be in the 'input' group. "
                + "This adds you with one admin prompt; you then log out and back in once "
                + "to activate it.",
                "Add me to the input group",
                $"sudo usermod -aG input {CurrentUser}"
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        if (!IsWayland || InputGroupCheck.CurrentUserInInputGroup() == true)
        {
            return new SetupActionOutcome(true, "Global shortcut already active.");
        }

        var manual = $"sudo usermod -aG input {CurrentUser}";
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupActionOutcome(
                false,
                "pkexec is not available to request admin rights.",
                $"Run this in a terminal instead: {manual}"
            );
        }

        var result = await _runner
            .RunAsync(
                "pkexec",
                new[] { "usermod", "-aG", "input", CurrentUser },
                timeout: TimeSpan.FromMinutes(2),
                ct: ct
            )
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return new SetupActionOutcome(
                true,
                "Added to the input group.",
                "Log out and back in (or reboot) once to activate the global shortcut."
            );
        }

        if (result.ExitCode is 126 or 127)
        {
            return new SetupActionOutcome(
                false,
                "Admin authorization was cancelled or denied.",
                $"You can also run: {manual}"
            );
        }

        return new SetupActionOutcome(
            false,
            $"Could not add you to the input group (exit {result.ExitCode}).",
            $"Run this in a terminal instead: {manual}"
        );
    }

    private static Task<SetupTaskState> Satisfied(string summary) =>
        Task.FromResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, summary));

    /// <summary>
    ///     True if the current user appears in the <c>input</c> line of
    ///     <c>/etc/group</c> — i.e. <c>usermod -aG</c> already ran, even though
    ///     the running process won't pick the membership up until a re-login.
    /// </summary>
    private static bool UserListedInInputGroupFile()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/group"))
            {
                if (!line.StartsWith("input:", StringComparison.Ordinal))
                {
                    continue;
                }

                var lastColon = line.LastIndexOf(':');
                if (lastColon < 0 || lastColon == line.Length - 1)
                {
                    return false;
                }

                var members = line[(lastColon + 1)..].Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                return Array.Exists(members, m => m == CurrentUser);
            }
        }
        catch
        {
            // Can't read the group file — fall through to "not listed".
        }

        return false;
    }
}
