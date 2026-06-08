namespace TypeWhisper.Linux.Services.Setup;

/// <summary>
///     Ensures <c>ffmpeg</c> is present so file transcription accepts a broad
///     range of audio/video formats. Recommended, not required: live dictation
///     works without it — only importing arbitrary media files is limited.
/// </summary>
public sealed class FfmpegSetupTask : ISetupTask
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly PackageInstaller _installer;

    public FfmpegSetupTask(SystemCommandAvailabilityService commands, PackageInstaller installer)
    {
        _commands = commands;
        _installer = installer;
    }

    public string Id => "ffmpeg";
    public string Title => "Audio conversion (ffmpeg)";
    public SetupTaskSeverity Severity => SetupTaskSeverity.Recommended;

    public bool AppliesToThisMachine() => true;

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        if (_commands.GetSnapshot().HasFfmpeg)
        {
            return Task.FromResult(
                new SetupTaskState(SetupTaskStatusKind.Satisfied, "ffmpeg available.")
            );
        }

        return Task.FromResult(
            new SetupTaskState(
                SetupTaskStatusKind.NeedsAction,
                "ffmpeg is not installed.",
                "Broadens the set of audio/video formats you can transcribe from a file.",
                "Install ffmpeg",
                _installer.BuildSudoCommand(new[] { "ffmpeg" })
            )
        );
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        var outcome = await _installer.InstallAsync(new[] { "ffmpeg" }, ct).ConfigureAwait(false);
        _commands.RefreshSnapshot();
        return outcome;
    }
}
