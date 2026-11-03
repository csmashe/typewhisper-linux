namespace TypeWhisper.PluginSDK.Processes;

public interface IPluginProcessSession : IDisposable, IAsyncDisposable
{
    int ProcessId { get; }

    bool IsRunning { get; }

    Task<ProcessExitOutcome> Completion { get; }

    IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Throws <see cref="IOException" /> once the child has exited, whether the pipe broke
    ///     (EPIPE) or the session was already reaped — callers race the exit, so the outcome
    ///     must not depend on which side won.
    /// </summary>
    ValueTask WriteStandardInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    );

    /// <summary>Closing an already-exited session's stdin is a no-op, not an error.</summary>
    ValueTask CompleteStandardInputAsync(
        CancellationToken cancellationToken = default
    );

    void Terminate();
}

public interface IPluginProcessSupervisor
{
    ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    );

    Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    );

    ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    );

    DetachedLaunchOutcome LaunchDetached(ProcessCommand command);

    DetachedLaunchOutcome LaunchUri(Uri uri);
}
