namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures a clipboard helper is installed — the universal fallback path
///     for getting transcribed text into a window when automatic paste isn't
///     available. The required package is session-derived: <c>wl-clipboard</c>
///     on Wayland, <c>xclip</c> on X11 (the capability snapshot already names
///     the right one for this machine).
/// </summary>
public sealed class ClipboardSetupTask : ISetupTask
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly PackageInstaller _installer;

    public ClipboardSetupTask(SystemCommandAvailabilityService commands, PackageInstaller installer)
    {
        _commands = commands;
        _installer = installer;
    }

    public string Id => "clipboard";
    public string Title => "Clipboard helper";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Required;

    public bool AppliesToThisMachine()
    {
        return true;
    }

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        var snapshot = _commands.GetSnapshot();
        var package = snapshot.ClipboardToolName;

        if (snapshot.HasClipboardTool)
        {
            return Task.FromResult(
                new SetupTaskState(SetupTaskStatusKind.Satisfied, $"{package} available.")
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                $"{package} is not installed.",
                "Needed so TypeWhisper can place text on the clipboard as a fallback.",
                $"Install {package}",
                _installer.BuildSudoCommand(new[] { package })
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        var package = _commands.GetSnapshot().ClipboardToolName;
        var outcome = await _installer.InstallAsync(new[] { package }, ct).ConfigureAwait(false);
        _commands.RefreshSnapshot();
        return outcome;
    }
}