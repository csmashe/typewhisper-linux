namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Importance of a setup task. <see cref="Required" /> tasks gate the
///     wizard's Finish button — the user cannot complete onboarding (without
///     explicitly skipping) until every applicable required task is satisfied.
///     <see cref="Recommended" /> tasks surface in the same checklist but never
///     block completion.
/// </summary>
public enum SetupTaskSeverity
{
    Required,
    Recommended
}

/// <summary>
///     The evaluated state of a setup task on this machine.
/// </summary>
public enum SetupTaskStatusKind
{
    /// <summary>Nothing to do — the capability is present and working.</summary>
    Satisfied,

    /// <summary>The task needs an action (auto-fix, open-a-page, or a manual command).</summary>
    NeedsAction,

    /// <summary>An action is currently running.</summary>
    Working,

    /// <summary>The last action failed; the user can retry or fall back to the manual command.</summary>
    Failed
}

/// <summary>
///     A snapshot of a task's current state, suitable for rendering. Pure
///     data — produced by <see cref="ISetupTask.EvaluateAsync" /> and after
///     <see cref="ISetupTask.RunActionAsync" />.
/// </summary>
/// <param name="Kind">Overall status used to drive the badge and gating.</param>
/// <param name="Summary">One-line description of the current state.</param>
/// <param name="Detail">Optional longer explanation / what the fix will do.</param>
/// <param name="ActionLabel">
///     Label for the action button (e.g. "Install ydotool", "Register shortcut",
///     "Open install page"). Null when there is no automatable action — the
///     user must follow <paramref name="CopyCommand" /> or external steps.
/// </param>
/// <param name="CopyCommand">
///     A copyable shell command shown when the fix can't be fully automated
///     (unknown package manager, browser-only install, etc.). Null when not
///     applicable.
/// </param>
public sealed record SetupTaskState(
    SetupTaskStatusKind Kind,
    string Summary,
    string? Detail = null,
    string? ActionLabel = null,
    string? CopyCommand = null
);

/// <summary>
///     Outcome of running a task's action. <see cref="Success" /> reports
///     whether the action itself completed without error — it does not imply
///     the task is now satisfied (e.g. opening a browser install page succeeds
///     but the capability only appears after the user clicks Install). The
///     caller re-evaluates the task to learn the new state.
/// </summary>
public sealed record SetupActionOutcome(bool Success, string Message, string? Detail = null);

/// <summary>
///     One unit of machine setup the onboarding wizard drives to completion.
///     Each task owns one capability (clipboard, automatic paste, the global
///     hotkey registration, active-window detection, …) and is fully
///     self-describing: it decides whether it applies to the current machine,
///     reports its own status, and performs its own fix. The wizard renders
///     whatever tasks apply and never hard-codes any one desktop or session —
///     adding support for a new environment is a matter of registering more
///     tasks (or teaching an existing task about it), not editing the wizard.
/// </summary>
public interface ISetupTask
{
    /// <summary>Stable identifier used in logs and as the row key.</summary>
    string Id { get; }

    /// <summary>Human-readable title shown as the checklist row heading.</summary>
    string Title { get; }

    /// <summary>Whether this task blocks completion when unsatisfied.</summary>
    SetupTaskSeverity Severity { get; }

    /// <summary>
    ///     Cheap, synchronous gate: does this task have anything to do on the
    ///     current machine? Tasks that don't apply (e.g. an X11-only paste
    ///     task on a Wayland session, or the GNOME extension task off GNOME)
    ///     are omitted from the checklist entirely. Must only read environment
    ///     variables and do binary lookups — no spawning helper commands.
    /// </summary>
    bool AppliesToThisMachine();

    /// <summary>
    ///     Evaluate the task's current state. May read the desktop's own
    ///     stores (gsettings, dbus, config files) so it is async, but should
    ///     stay quick — the checklist evaluates all tasks together.
    /// </summary>
    Task<SetupTaskState> EvaluateAsync(CancellationToken ct);

    /// <summary>
    ///     Perform the task's action (the thing behind the action button):
    ///     install a package via pkexec, write a gsettings shortcut, open an
    ///     install page, etc. Returns the immediate outcome; the caller
    ///     re-evaluates afterward to get the new state.
    /// </summary>
    Task<SetupActionOutcome> RunActionAsync(CancellationToken ct);
}
