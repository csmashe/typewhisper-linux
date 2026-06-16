using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures TypeWhisper can read the focused window's app/title/URL for per-app profile
///     matching. On GNOME this requires the "Window Calls" shell extension (browser install).
///     Two-step: open install page, then verify via D-Bus probe — never marks done on trust.
///     Recommended (not required); other desktops expose window info natively.
/// </summary>
public sealed class ActiveWindowSetupTask : ISetupTask
{
    private readonly GnomeWindowCallsSetupHelper _helper;

    // Flips the action label from "Open install page" to "Check installation" after the page opens.
    private bool _installPageOpened;

    public ActiveWindowSetupTask(GnomeWindowCallsSetupHelper helper)
    {
        _helper = helper;
    }

    public string Id => "active-window";
    public string Title => Loc.Instance["Setup.ActiveWindowTitle"];
    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine()
    {
        return _helper.IsApplicable();
    }

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        if (_helper.IsCurrentlyInstalled())
        {
            return Task.FromResult(
                new SetupTaskState(
                    SetupTaskStatusKind.Satisfied,
                    Loc.Instance["Setup.ActiveWindowInstalled"]
                )
            );
        }

        // Only the real D-Bus probe (re-evaluation after the action) can flip this to Satisfied.
        return Task.FromResult(
            _installPageOpened
                ? new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    Loc.Instance["Setup.ActiveWindowWaiting"],
                    Loc.Instance["Setup.ActiveWindowWaitingDetail"],
                    Loc.Instance["Setup.ActiveWindowCheckInstallation"]
                )
                : new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    Loc.Instance["Setup.ActiveWindowNotInstalled"],
                    Loc.Instance["Setup.ActiveWindowNotInstalledDetail"],
                    Loc.Instance["Setup.ActiveWindowOpenInstallPage"]
                )
        );
    }

    public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        // Re-evaluation after this action will mark done if it's now installed.
        if (_helper.IsCurrentlyInstalled())
        {
            return Task.FromResult(new SetupActionOutcome(true, Loc.Instance["Setup.ActiveWindowDetected"]));
        }

        // D-Bus probe still not responding after install — report honestly, task stays NeedsAction.
        if (_installPageOpened)
        {
            return Task.FromResult(
                new SetupActionOutcome(
                    false,
                    Loc.Instance["Setup.ActiveWindowNotDetectedYet"],
                    Loc.Instance["Setup.ActiveWindowNotDetectedYetDetail"]
                )
            );
        }

        var opened = _helper.TryOpenInstallPage();
        _installPageOpened = opened;
        return Task.FromResult(
            opened
                ? new SetupActionOutcome(
                    true,
                    Loc.Instance["Setup.ActiveWindowOpenedInstallPage"],
                    Loc.Instance["Setup.ActiveWindowOpenedInstallPageDetail"]
                )
                : new SetupActionOutcome(
                    false,
                    Loc.Instance["Setup.ActiveWindowCouldNotOpenInstallPage"],
                    Loc.Instance["Setup.ActiveWindowCouldNotOpenInstallPageDetail"]
                )
        );
    }
}