namespace TypeWhisper.PluginSDK.Processes;

public interface IPluginProcessSession : IDisposable, IAsyncDisposable
{
    int ProcessId { get; }

    bool IsRunning { get; }

    Task<ProcessExitOutcome> Completion { get; }

    IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
        CancellationToken cancellationToken = default
    );

    ValueTask WriteStandardInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    );

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
