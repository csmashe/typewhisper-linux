using System.Text;

namespace TypeWhisper.PluginSDK.Processes;

public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null
);

public abstract record ProcessInput;

public sealed record Utf8ProcessInput(string Value) : ProcessInput;

public sealed record BinaryProcessInput(ReadOnlyMemory<byte> Value) : ProcessInput;

public enum ProcessCaptureMode
{
    Discard,
    Utf8Text,
    Binary,
}

public enum ProcessPostExitPipePolicy
{
    RequireEof,
    AbandonAfterGrace,
}

public sealed record ProcessOneShotOptions(
    TimeSpan? Timeout = null,
    ProcessInput? StandardInput = null,
    ProcessCaptureMode StandardOutput = ProcessCaptureMode.Utf8Text,
    ProcessCaptureMode StandardError = ProcessCaptureMode.Utf8Text,
    ProcessPostExitPipePolicy PostExitPipePolicy = ProcessPostExitPipePolicy.RequireEof,
    TimeSpan? PostExitDrainGrace = null
)
{
    /// <summary>
    /// Ceiling on captured stdout bytes, or null for unbounded. Must not be negative. Once a
    /// stream passes its ceiling the supervisor terminates the child and reports
    /// <see cref="ProcessRunStatus.OutputLimitExceeded" /> with truncated captured output.
    /// </summary>
    public int? MaximumStandardOutputBytes { get; init; }

    /// <summary>The same ceiling for stderr.</summary>
    public int? MaximumStandardErrorBytes { get; init; }

    /// <summary>
    /// Start with an empty environment so only <see cref="ProcessCommand.Environment" />
    /// reaches the child. Defaults to false to preserve inherited environment behavior.
    /// </summary>
    public bool ClearInheritedEnvironment { get; init; }
}

public enum ProcessRunStatus
{
    Exited,
    TimedOut,
    StartFailed,
    OutputLimitExceeded,
}

public enum ProcessOutputStatus
{
    Complete,
    AbandonedAfterExit,

    /// <summary>The pumps were abandoned at a byte ceiling, so the capture stops mid-stream.</summary>
    Truncated,
}

/// <param name="ExitCode">
///     The child's exit code, or null when it never exited on its own — including a
///     <see cref="ProcessRunStatus.OutputLimitExceeded" /> run that had to terminate it.
/// </param>
public sealed record ProcessRunOutcome(
    ProcessRunStatus Status,
    int? ExitCode,
    byte[] StandardOutput,
    byte[] StandardError,
    ProcessOutputStatus OutputStatus,
    string? StartError
)
{
    public bool Succeeded => Status == ProcessRunStatus.Exited && ExitCode == 0;

    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);

    public string StandardErrorText => Encoding.UTF8.GetString(StandardError);
}

public enum ProcessSessionOutputMode
{
    Discard,
    Lines,
}

public sealed record ProcessSessionOptions(
    bool RedirectStandardInput = false,
    ProcessSessionOutputMode StandardOutput = ProcessSessionOutputMode.Discard,
    ProcessSessionOutputMode StandardError = ProcessSessionOutputMode.Discard
);

public enum ProcessStream
{
    StandardOutput,
    StandardError,
}

public sealed record ProcessOutputLine(ProcessStream Stream, string Text);

public enum ProcessExitReason
{
    Exited,
    Terminated,
}

public sealed record ProcessExitOutcome(ProcessExitReason Reason, int? ExitCode);

public sealed record ProcessSessionStartOutcome(
    IPluginProcessSession? Session,
    string? StartError
)
{
    public bool Started => Session is not null;
}

public sealed record DetachedLaunchOutcome(bool Started, string? StartError);
