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
    public string Title => "Active-window detection";
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
                    "Window Calls extension installed."
                )
            );
        }

        // Only the real D-Bus probe (re-evaluation after the action) can flip this to Satisfied.
        return Task.FromResult(
            _installPageOpened
                ? new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    "Waiting for the Window Calls extension.",
                    "Once you've clicked Install in the browser, check it here.",
                    "Check installation"
                )
                : new SetupTaskState(
                    SetupTaskStatusKind.NeedsAction,
                    "Window Calls GNOME extension not installed.",
                    "Lets profiles match the focused app/URL. Opens the install page "
                    + "in your browser; click Install there, then come back and check.",
                    "Open install page"
                )
        );
    }

    public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        // Re-evaluation after this action will mark done if it's now installed.
        if (_helper.IsCurrentlyInstalled())
        {
            return Task.FromResult(new SetupActionOutcome(true, "Window Calls extension detected."));
        }

        // D-Bus probe still not responding after install — report honestly, task stays NeedsAction.
        if (_installPageOpened)
        {
            return Task.FromResult(
                new SetupActionOutcome(
                    false,
                    "Not detected yet.",
                    "Make sure you clicked Install (and enabled it), then check again. "
                    + "A GNOME Shell reload / re-login may be needed."
                )
            );
        }

        var opened = _helper.TryOpenInstallPage();
        _installPageOpened = opened;
        return Task.FromResult(
            opened
                ? new SetupActionOutcome(
                    true,
                    "Opened the Window Calls install page.",
                    "Click Install there, then come back and use Check installation."
                )
                : new SetupActionOutcome(
                    false,
                    "Could not open the install page.",
                    "Visit extensions.gnome.org and search for \"Window Calls\"."
                )
        );
    }
}