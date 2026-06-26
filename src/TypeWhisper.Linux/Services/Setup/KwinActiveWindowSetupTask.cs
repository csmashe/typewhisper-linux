using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Installs <c>kdotool</c> on KDE Plasma Wayland, where KWin exposes no
///     unprivileged active-window API. Without it, per-app profiles can't match
///     and dictations aren't attributed to any app. Recommended but not required.
///     Wayland-only — KDE X11 uses <see cref="ActiveWindow.XdotoolActiveWindowProvider" />.
/// </summary>
public sealed class KwinActiveWindowSetupTask : ISetupTask
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly PackageInstaller _installer;

    public KwinActiveWindowSetupTask(
        SystemCommandAvailabilityService commands,
        PackageInstaller installer
    )
    {
        _commands = commands;
        _installer = installer;
    }

    public string Id => "active-window-kde";
    public string Title => Loc.Instance["Setup.ActiveWindowTitle"];
    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine()
    {
        // Gate on compositor + session type so this never surfaces on GNOME or KDE X11.
        var snapshot = _commands.GetSnapshot();
        return snapshot is { SessionType: "Wayland", Compositor: "kde" };
    }

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        if (DesktopDetector.BinaryExists("kdotool"))
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.Satisfied,
                    Loc.Instance["Setup.KdotoolAvailable"]
                )
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                Loc.Instance["Setup.KdotoolNotInstalled"],
                Loc.Instance["Setup.KdotoolNotInstalledDetail"],
                Loc.Instance["Setup.KdotoolInstall"],
                _installer.BuildSudoCommand(new[] { "kdotool" })
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        var outcome = await _installer.InstallAsync(new[] { "kdotool" }, ct).ConfigureAwait(false);
        _commands.RefreshSnapshot();
        return outcome;
    }
}