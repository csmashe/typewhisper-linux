using TypeWhisper.Linux.Services.Insertion;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures automatic paste (typing the transcript into the focused window) works.
///     On Wayland installs and configures <c>ydotool</c>, which injects via the kernel's
///     uinput device and reaches both native-Wayland and XWayland windows. (wtype only
///     reaches native-Wayland windows; it can't type into XWayland apps.) On X11
///     installs <c>xdotool</c>. The Wayland path chains install + udev/service setup
///     (admin rights required) in one click.
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

    private bool IsWayland => _commands.GetSnapshot().SessionType == "Wayland";

    public string Id => "auto-paste";
    public string Title => Loc.Instance["Setup.AutoPasteTitle"];
    public SetupTaskSeverity Severity => SetupTaskSeverity.Required;

    public bool AppliesToThisMachine()
    {
        return true;
    }

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        var snapshot = _commands.GetSnapshot();

        // TextInsertionService.BuildChain and this task use different usable-tool
        // policies: BuildChain demotes wtype to a fallback when the compositor rejects
        // it, while this task requires it usable outright; xdotool is excluded on Wayland.
        var autoPasteUsable = IsWayland
            ? snapshot.HasYdotoolAvailable
              || snapshot is { HasWtype: true, CompositorRejectsWtype: false }
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
                    Loc.Instance["Setup.XdotoolNotInstalled"],
                    Loc.Instance["Setup.XdotoolNotInstalledDetail"],
                    Loc.Instance["Setup.XdotoolInstall"],
                    _installer.BuildSudoCommand(["xdotool"])
                )
            );
        }

        var status = _ydotool.IsCurrentlyConfigured();
        if (!status.BinaryInstalled)
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    Loc.Instance["Setup.YdotoolNotInstalled"],
                    Loc.Instance["Setup.YdotoolNotInstalledDetail"],
                    Loc.Instance["Setup.YdotoolInstallAndSetUp"],
                    _installer.BuildSudoCommand(["ydotool"])
                )
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                Loc.Instance["Setup.YdotoolNotConfigured"],
                Loc.Instance["Setup.YdotoolNotConfiguredDetail"],
                Loc.Instance["Setup.YdotoolSetUp"]
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        if (!IsWayland)
        {
            var outcome = await _installer
                .InstallAsync(["xdotool"], ct)
                .ConfigureAwait(false);
            _commands.RefreshSnapshot();
            return outcome;
        }

        // Wayland: install ydotool if missing, then run udev + service setup.
        if (!_ydotool.IsCurrentlyConfigured().BinaryInstalled)
        {
            var install = await _installer
                .InstallAsync(["ydotool"], ct)
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
                Loc.Instance.GetString("Setup.AutoPasteSetupFailed", ex.Message),
                Loc.Instance["Setup.AutoPasteSetupFailedDetail"]
            );
        }

        _commands.RefreshSnapshot();
        return result;
    }
}