using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.PluginSystem.Tests;

// Shared by the plugin-host, OpenAI, xAI, and Supertonic TTS suites.

internal sealed class RecordingPluginProcessSupervisor : IProcessRunner
{
    public ControlledPluginProcessSession? NextSession { get; set; }
    public List<(ProcessCommand Command, ProcessSessionOptions Options)> Sessions { get; } = [];
    public List<Uri> Uris { get; } = [];
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- settable knob on the fake, mirroring NextSession; narrowing it would make it unsettable
    public ProcessRunOutcome OneShotOutcome { get; set; } = new(
        ProcessRunStatus.Exited,
        0,
        [],
        [],
        ProcessOutputStatus.Complete,
        null
    );

    // Honours the token like the real runner, so scope-lifetime cancellation is observable.
    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OneShotOutcome;
    }

    public Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OneShotOutcome);
    }

    /// <summary>Fires mid-launch so tests can interleave a scope stop with registration.</summary>
    public Action? OnStartSession { get; set; }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        Sessions.Add((command, options));
        OnStartSession?.Invoke();
        return NextSession is { } session
            ? new ProcessSessionStartOutcome(session, null)
            : new ProcessSessionStartOutcome(null, "fake start failure");
    }

    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command) =>
        new(true, null);

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        Uris.Add(uri);
        return new DetachedLaunchOutcome(true, null);
    }

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    ) => throw new NotSupportedException();
}

internal sealed class ControlledPluginProcessSession : IPluginProcessSession
{
    private readonly TaskCompletionSource<ProcessExitOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int TerminateCount { get; private set; }
    public int ProcessId => 1234;
    public bool IsRunning => !_completion.Task.IsCompleted;
    public Task<ProcessExitOutcome> Completion => _completion.Task;

    public async IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default
    )
    {
        await Completion.WaitAsync(cancellationToken);
        yield break;
    }

    public ValueTask WriteStandardInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public ValueTask CompleteStandardInputAsync(
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public void Terminate()
    {
        TerminateCount++;
    }

    public void Dispose()
    {
        Terminate();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Complete(ProcessExitOutcome outcome)
    {
        _completion.TrySetResult(outcome);
    }
}

internal static class ProcessTestWait
{
    // Stopwatch, not DateTime.UtcNow: a wall-clock step mid-poll would otherwise cut the
    // budget short or stretch it.
    public static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met before the deadline.");
    }
}
