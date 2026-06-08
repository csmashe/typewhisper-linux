using TypeWhisper.Linux.Services.Insertion;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures automatic paste — typing the transcript straight into the
///     focused window — works on this machine. The mechanism is
///     session-specific: on Wayland we install and configure <c>ydotool</c>
///     (the only path that works under GNOME/KDE, which reject wtype's
///     virtual-keyboard protocol); on X11 we install <c>xdotool</c>. The fix
///     can chain two steps in one click on Wayland — install the package, then
///     run the udev-rule + service setup (which prompts for admin rights).
/// </summary>
public sealed class AutoPasteSetupTask : ISetupTask
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly PackageInstaller _installer;
    private readonly YdotoolSetupHelper _ydotool;

    public AutoPasteSetupTask(
        SystemCommandAvailabilityService commands,
        PackageInstaller installer,
        YdotoolSetupHelper ydotool
    )
    {
        _commands = commands;
        _installer = installer;
        _ydotool = ydotool;
    }

    public string Id => "auto-paste";
    public string Title => "Automatic paste";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Required;

    public bool AppliesToThisMachine() => true;

    private bool IsWayland => _commands.GetSnapshot().SessionType == "Wayland";

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        var snapshot = _commands.GetSnapshot();

        // Don't trust the broad snapshot.HasAutomaticPasteTool flag: it counts
        // wtype as available on Wayland even on GNOME/KDE, where wtype's
        // virtual-keyboard protocol is rejected (snapshot.CompositorRejectsWtype)
        // and the insertion chain demotes it — so that flag can read "ready"
        // while native-window paste is actually broken. xdotool on Wayland only
        // reaches XWayland apps, not native ones, so it doesn't count here
        // either. Require a path that genuinely types into native windows.
        var autoPasteUsable = IsWayland
            ? snapshot.HasYdotoolAvailable
              || (snapshot.HasWtype && !snapshot.CompositorRejectsWtype)
            : snapshot.HasXdotool;

        if (autoPasteUsable)
        {
            return Task.FromResult(
                new SetupTaskState(SetupTaskStatusKind.Satisfied, snapshot.PasteStatus)
            );
        }

        if (!IsWayland)
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    "xdotool is not installed.",
                    "Needed to type the transcript into the focused window on X11.",
                    "Install xdotool",
                    _installer.BuildSudoCommand(new[] { "xdotool" })
                )
            );
        }

        var status = _ydotool.IsCurrentlyConfigured();
        if (!status.BinaryInstalled)
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    "ydotool is not installed.",
                    "ydotool is the reliable way to type into Wayland windows on GNOME/KDE. "
                    + "This installs it, then configures its udev rule and background service "
                    + "(you'll be asked for your admin password).",
                    "Install & set up ydotool",
                    _installer.BuildSudoCommand(new[] { "ydotool" })
                )
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                "ydotool is installed but not configured.",
                "Configures the udev rule and background service so ydotool can type "
                + "(you'll be asked for your admin password).",
                "Set up ydotool"
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        if (!IsWayland)
        {
            var outcome = await _installer
                .InstallAsync(new[] { "xdotool" }, ct)
                .ConfigureAwait(false);
            _commands.RefreshSnapshot();
            return outcome;
        }

        // Wayland: install ydotool first if the binary is missing, then run
        // the (possibly admin-prompting) udev + service setup.
        if (!_ydotool.IsCurrentlyConfigured().BinaryInstalled)
        {
            var install = await _installer
                .InstallAsync(new[] { "ydotool" }, ct)
                .ConfigureAwait(false);
            _commands.RefreshSnapshot();
            if (!install.Success)
            {
                return install;
            }
        }

        SetupActionOutcome result;
        try
        {
            var setup = await _ydotool.SetUpAsync(ct).ConfigureAwait(false);
            result = new SetupActionOutcome(setup.Success, setup.Message, setup.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new SetupActionOutcome(
                false,
                $"Setup failed: {ex.Message}",
                "You can retry, or finish setup from the Text insertion section later."
            );
        }

        _commands.RefreshSnapshot();
        return result;
    }
}
