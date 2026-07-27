using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures the dictation hotkey fires globally with full tap-vs-hold support.
///     On X11 the in-app hook is already global — nothing to do.
///     On Wayland the compositor won't deliver global keys to the app, so it reads
///     keyboard input directly via evdev. That needs read access to keyboard event
///     nodes, which this task grants by installing a small <c>uaccess</c> udev rule
///     (<see cref="InputAccessSetupHelper" />): one admin prompt, access applied to
///     the active session immediately — no logout or reboot. On init systems
///     without logind the rule's <c>GROUP="input"</c> fallback additionally needs
///     the user to join the <c>input</c> group and re-login, which this task falls
///     back to automatically.
///     Desktop "custom shortcuts" (gsettings / KDE / compositor binds) are deliberately
///     avoided as the default: they fire a single pulse per press with no release event,
///     making hold-to-talk impossible and autorepeat thrashing start/stop rapidly. They
///     remain available as a documented alternative in Settings → Shortcuts.
/// </summary>
public sealed class GlobalHotkeySetupTask : ISetupTask
{
    private readonly IProcessRunner _runner;
    private readonly InputAccessSetupHelper _accessHelper;

    // Seams kept behind an internal constructor so the production wiring uses the
    // real session probe / device probe / group-file read / backend hot-swap,
    // while tests drive the state machine deterministically without touching the
    // session environment, /dev/input, /etc/group, or resolving a live backend.
    private readonly Func<bool> _isWayland;
    private readonly Func<bool> _hasKeyboardAccess;
    private readonly Func<bool> _isSeatManagerPresent;
    private readonly Func<bool> _userListedInInputGroupFile;
    private readonly Func<bool> _ruleInstalled;
    private readonly Func<CancellationToken, Task> _onAccessGranted;

    public GlobalHotkeySetupTask(
        SystemCommandAvailabilityService commands,
        IProcessRunner runner,
        InputAccessSetupHelper accessHelper,
        HotkeyService hotkey
    )
        : this(
            () => commands.GetSnapshot().SessionType == "Wayland",
            runner,
            accessHelper,
            InputDeviceAccessCheck.HasKeyboardAccess,
            InputAccessSetupHelper.IsSeatManagerPresent,
            UserListedInInputGroupFile,
            InputAccessSetupHelper.IsRuleInstalled,
            hotkey.SwitchBackendAsync
        )
    {
    }

    internal GlobalHotkeySetupTask(
        Func<bool> isWayland,
        IProcessRunner runner,
        InputAccessSetupHelper accessHelper,
        Func<bool> hasKeyboardAccess,
        Func<bool> isSeatManagerPresent,
        Func<bool> userListedInInputGroupFile,
        Func<bool> ruleInstalled,
        Func<CancellationToken, Task> onAccessGranted
    )
    {
        _isWayland = isWayland;
        _runner = runner;
        _accessHelper = accessHelper;
        _hasKeyboardAccess = hasKeyboardAccess;
        _isSeatManagerPresent = isSeatManagerPresent;
        _userListedInInputGroupFile = userListedInInputGroupFile;
        _ruleInstalled = ruleInstalled;
        _onAccessGranted = onAccessGranted;
    }

    private bool IsWayland => _isWayland();

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
        // X11: in-app hook captures global keys with press/release — no setup needed.
        if (!IsWayland)
        {
            return Satisfied(Loc.Instance["Setup.GlobalHotkeyActiveX11"]);
        }

        // Wayland: gate on actual openability of a keyboard node, NOT input-group
        // membership. With the uaccess rule the user is granted access via a
        // session ACL without ever joining the group, so a group check would
        // falsely report "no access" and nag.
        if (_hasKeyboardAccess())
        {
            return Satisfied(Loc.Instance["Setup.GlobalHotkeyActiveEvdev"]);
        }

        // Non-logind fallback: we already installed the uaccess rule and fell back
        // to the input group, so only a re-login remains — satisfied-with-caveat so
        // Finish isn't blocked. Gated on the rule actually being installed: a user
        // with *stale* group membership from the old usermod flow (rule not yet
        // installed) should instead be offered the new rule, which can grant access
        // immediately on logind rather than forcing a logout.
        if (_userListedInInputGroupFile() && _ruleInstalled())
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
                InputAccessSetupHelper.ManualInstallCommand()
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        if (!IsWayland || _hasKeyboardAccess())
        {
            return new SetupActionOutcome(true, Loc.Instance["Setup.GlobalHotkeyAlreadyActive"]);
        }

        // ManualInstallCommand() already carries the non-logind input-group fallback
        // as a trailing comment, so the copyable command is complete on every system.
        var manual = InputAccessSetupHelper.ManualInstallCommand();
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupActionOutcome(
                false,
                Loc.Instance["Setup.PkexecUnavailable"],
                Loc.Instance.GetString("Setup.RunInTerminalInstead", manual)
            );
        }

        // Install the keyboard-access udev rule (one admin prompt) and apply it to
        // the current session via udevadm reload + trigger + settle.
        var install = await _accessHelper.InstallAsync(ct).ConfigureAwait(false);
        if (!install.Success)
        {
            // A refusal (foreign file / symlink at our path) must NOT hand the user
            // the manual command aimed at the file the guard protected — surface the
            // refusal's own message + detail (they say to move/rename it) instead.
            if (install.Refused)
            {
                return new SetupActionOutcome(false, install.Message, install.Detail);
            }

            return new SetupActionOutcome(
                false,
                install.Cancelled
                    ? Loc.Instance["Setup.AdminAuthCancelled"]
                    : Loc.Instance["Setup.GlobalHotkeyAccessFailed"],
                Loc.Instance.GetString("Setup.RunInTerminalInstead", manual)
            );
        }

        // A failed re-probe doesn't prove logind is absent (udev may still be
        // settling, session inactive, no keyboard connected) — never broaden to
        // the input group while a seat manager is present.
        if (!_hasKeyboardAccess())
        {
            if (_isSeatManagerPresent())
            {
                return new SetupActionOutcome(
                    false,
                    Loc.Instance["Shortcuts.KeyboardAccessNotConfirmed"],
                    Loc.Instance["Shortcuts.KeyboardAccessNotConfirmedDetail"]
                );
            }

            // Genuine non-logind host (Devuan, Alpine without elogind).
            return await AddToInputGroupFallbackAsync(ct).ConfigureAwait(false);
        }

        // Self-correcting: on logind systems the uaccess ACL is live now, so the
        // re-probe succeeds and we're done with no reboot. Hot-swap the backend so
        // evdev re-attaches the now-readable devices without an app restart.
        try
        {
            await _onAccessGranted(ct).ConfigureAwait(false);
        }
        catch
        {
            // A backend hot-swap hiccup must not turn a successful grant into a
            // failure — the access is granted; the backend re-probes on next
            // launch regardless.
        }

        return new SetupActionOutcome(true, Loc.Instance["Setup.GlobalHotkeyAccessEnabled"]);
    }

    /// <summary>
    ///     Non-logind last resort: add the user to the <c>input</c> group so the
    ///     rule's <c>GROUP="input"</c> clause grants access after a re-login. Only
    ///     reached after the shared seat-directory check confirms that neither
    ///     systemd-logind nor elogind is present.
    /// </summary>
    private async Task<SetupActionOutcome> AddToInputGroupFallbackAsync(CancellationToken ct)
    {
        var manual = $"sudo usermod -aG input {CurrentUser}";

        var result = await _runner
            .RunAsync(
                "pkexec",
                ["usermod", "-aG", "input", CurrentUser],
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
    ///     Used only for the non-logind relogin-pending caveat.
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
