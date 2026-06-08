namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures TypeWhisper can read the focused window's app/title (and browser
///     URL) so per-app profiles can match. On GNOME this needs the "Window
///     Calls" shell extension, which can only be installed from the browser.
///     The task is a verified two-step: first "Open install page", then — after
///     the user installs it — "Check installation", which only marks the task
///     done once the extension's D-Bus object actually responds. It never marks
///     off on trust. Recommended rather than required: dictation works without
///     it, only window-aware profile switching is affected. The task simply
///     doesn't apply off GNOME (other desktops expose window info natively).
/// </summary>
public sealed class ActiveWindowSetupTask : ISetupTask
{
    private readonly GnomeWindowCallsSetupHelper _helper;

    // Set once we've opened the install page, so the action flips from
    // "Open install page" to "Check installation" — the user does the manual
    // browser install in between, then clicks to verify.
    private bool _installPageOpened;

    public ActiveWindowSetupTask(GnomeWindowCallsSetupHelper helper)
    {
        _helper = helper;
    }

    public string Id => "active-window";
    public string Title => "Active-window detection";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine() => _helper.IsApplicable();

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

        // After opening the page, the action becomes an explicit verification
        // step — clicking it re-probes and only the real check (in
        // EvaluateAsync, run right after) can flip this to Satisfied.
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
        // If it's already there (e.g. the user installed it), the follow-up
        // re-evaluation will mark the task done — don't reopen the page.
        if (_helper.IsCurrentlyInstalled())
        {
            return Task.FromResult(new SetupActionOutcome(true, "Window Calls extension detected."));
        }

        // Verification step: they said they installed it but the D-Bus probe
        // still doesn't see it. Report that honestly; the task stays NeedsAction.
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
