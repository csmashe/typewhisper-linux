using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Localization;

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
    public string Title => Loc.Instance["Setup.GlobalHotkeyTitle"];
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
            return Satisfied(Loc.Instance["Setup.GlobalHotkeyActiveX11"]);
        }

        // Wayland: evdev global hotkey requires the input group.
        if (InputGroupCheck.CurrentUserInInputGroup() == true)
        {
            return Satisfied(
                Loc.Instance["Setup.GlobalHotkeyActiveEvdev"]
            );
        }

        // usermod ran but the new group isn't effective until re-login.
        // Treat as satisfied-with-caveat so Finish isn't blocked.
        if (UserListedInInputGroupFile())
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.Satisfied,
                    Loc.Instance["Setup.GlobalHotkeyAddedRelogin"]
                )
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                Loc.Instance["Setup.GlobalHotkeyNeedsInputGroup"],
                Loc.Instance["Setup.GlobalHotkeyNeedsInputGroupHint"],
                Loc.Instance["Setup.GlobalHotkeyAddMeButton"],
                $"sudo usermod -aG input {CurrentUser}"
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        if (!IsWayland || InputGroupCheck.CurrentUserInInputGroup() == true)
        {
            return new SetupActionOutcome(true, Loc.Instance["Setup.GlobalHotkeyAlreadyActive"]);
        }

        var manual = $"sudo usermod -aG input {CurrentUser}";
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.PkexecUnavailable"],
                Loc.Instance.GetString("Setup.RunInTerminalInstead", manual)
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
                Loc.Instance["Setup.GlobalHotkeyAddedToGroup"],
                Loc.Instance["Setup.GlobalHotkeyReloginToActivate"]
            );
        }

        if (result.ExitCode is 126 or 127)
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.AdminAuthCancelled"],
                Loc.Instance.GetString("Setup.YouCanAlsoRun", manual)
            );
        }

        return new SetupActionOutcome(
            false,
            Loc.Instance.GetString("Setup.GlobalHotkeyAddFailed", result.ExitCode),
            Loc.Instance.GetString("Setup.RunInTerminalInstead", manual)
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