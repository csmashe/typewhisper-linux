using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures the dictation hotkey fires globally with full tap-vs-hold support.
///     On X11 the in-app hook is already global — nothing to do.
///     On Wayland the compositor won't deliver global keys to the app, so it reads
///     input devices directly via evdev (requires the <c>input</c> group; one admin
///     prompt + re-login).
///     Desktop "custom shortcuts" (gsettings / KDE / compositor binds) are deliberately
///     avoided: they fire a single pulse per press with no release event, making
///     hold-to-talk impossible and autorepeat thrashing start/stop rapidly.
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

    private bool IsWayland => _commands.GetSnapshot().SessionType == "Wayland";

    private static string CurrentUser => Environment.UserName;

    public string Id => "global-hotkey";
    public string Title => "Global dictation shortcut";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Required;

    public bool AppliesToThisMachine()
    {
        return true;
    }

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        // X11: in-app hook captures global keys with press/release — no group needed.
        if (!IsWayland)
        {
            return Satisfied("Global shortcut active. Tap toggles; hold for push-to-talk.");
        }

        // Wayland: evdev global hotkey requires the input group.
        if (InputGroupCheck.CurrentUserInInputGroup() == true)
        {
            return Satisfied(
                "Global shortcut active via evdev. Tap toggles; hold for push-to-talk."
            );
        }

        // usermod ran but the new group isn't effective until re-login.
        // Treat as satisfied-with-caveat so Finish isn't blocked.
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

    private static Task<SetupTaskState> Satisfied(string summary)
    {
        return Task.FromResult(new SetupTaskState(SetupTaskStatusKind.Satisfied, summary));
    }

    /// <summary>
    ///     True when the current user is listed in <c>/etc/group</c> for the <c>input</c>
    ///     group — i.e. <c>usermod -aG</c> ran but the session hasn't been restarted yet.
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