using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Per-plugin lifecycle scope over the singleton process runner. This is lifecycle
///     containment and diagnostics plumbing, not a security sandbox.
/// </summary>
public sealed class PluginProcessSupervisorScope(
    string pluginId,
    IProcessRunner processRunner
) : IPluginProcessSupervisor
{
    private readonly Lock _gate = new();
    private readonly HashSet<ScopedSession> _sessions = [];

    // Scope lifetime, linked into every one-shot so stopping reaches work that has no session
    // to visit. Never disposed: stopping is idempotent and can run again after unload, and a
    // source with no timer or registrations holds nothing that needs releasing.
    private CancellationTokenSource _lifetime = new();
    private bool _retired;

    // Bumped by every stop. A session that starts while a sweep is in flight would otherwise
    // register behind the snapshot and survive it, so it compares generations before joining.
    private long _stopGeneration;

    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        using var scoped = LinkLifetime(cancellationToken);
        return processRunner.RunProbe(command, options, scoped.Token);
    }

    public async Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        using var scoped = LinkLifetime(cancellationToken);
        return await processRunner
            .RunOneShotAsync(command, options, scoped.Token)
            .ConfigureAwait(false);
    }

    private CancellationTokenSource LinkLifetime(CancellationToken cancellationToken)
    {
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            lifetime = _lifetime;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token
        );
    }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        long generationAtStart;
        lock (_gate)
        {
            // Refused before the handoff: an already-retired scope must not let the command
            // run at all, since it could fork a descendant before we could terminate it.
            if (_retired)
            {
                return new ProcessSessionStartOutcome(null, RetiredMessage);
            }

            generationAtStart = _stopGeneration;
        }

        var started = processRunner.StartSession(command, options);
        if (started.Session is not { } session)
        {
            return started;
        }

        var scoped = new ScopedSession(pluginId, session, Remove);
        bool retired;
        bool swept;
        lock (_gate)
        {
            retired = _retired;
            swept = _stopGeneration != generationAtStart;
            if (!retired && !swept)
            {
                _sessions.Add(scoped);
            }
        }

        if (retired || swept)
        {
            // Started while the scope was being torn down, so it missed the termination
            // sweep; stopping it here is what keeps it from outliving the plugin.
            scoped.Terminate();
            // The caller never receives this session, so nobody else can observe its
            // completion task; leaving it unobserved would strand the fault.
            scoped.ObserveCompletion();
            return new ProcessSessionStartOutcome(
                null,
                retired ? RetiredMessage : StoppedWhileStartingMessage
            );
        }

        scoped.ObserveCompletion();
        return new ProcessSessionStartOutcome(scoped, null);
    }

    // A detached child is never tracked, so refusing it is the only containment a retired
    // scope has left. The gate is held across the handoff because a retirement interleaving
    // between the check and the launch would sweep nothing and leave the child behind.
    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command)
    {
        lock (_gate)
        {
            return _retired
                ? new DetachedLaunchOutcome(false, RetiredMessage)
                : processRunner.LaunchDetached(command);
        }
    }

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        lock (_gate)
        {
            return _retired
                ? new DetachedLaunchOutcome(false, RetiredMessage)
                : processRunner.LaunchUri(uri);
        }
    }

    private const string RetiredMessage = "The plugin process scope has been retired.";

    private const string StoppedWhileStartingMessage =
        "The plugin process scope was stopped while this session was starting.";

    /// <summary>
    ///     Stops everything running now but leaves the scope usable: a plugin whose
    ///     deactivation failed stays registered and must not be stranded on a dead scope.
    /// </summary>
    public void TerminateAll()
    {
        Stop(retire: false);
    }

    /// <summary>
    ///     Permanently closes the scope on unload or host shutdown. Later launches are
    ///     refused so work starting concurrently with teardown cannot outlive the plugin.
    /// </summary>
    public void Retire()
    {
        Stop(retire: true);
    }

    private void Stop(bool retire)
    {
        ScopedSession[] sessions;
        CancellationTokenSource stopped;
        lock (_gate)
        {
            stopped = _lifetime;
            _retired |= retire;
            _stopGeneration++;
            // A retired scope keeps its cancelled source so later one-shots fail fast, even
            // if a plain terminate arrives afterwards; otherwise a fresh source is installed
            // because the plugin may stay loaded.
            if (!_retired)
            {
                _lifetime = new CancellationTokenSource();
            }

            sessions = [.. _sessions];
        }

        // Cancelling reaps in-flight one-shots, which have no session to visit.
        stopped.Cancel();
        foreach (var session in sessions)
        {
            session.Terminate();
        }
    }

    internal int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    private void Remove(ScopedSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }

    private sealed class ScopedSession(
        string pluginId,
        IPluginProcessSession inner,
        Action<ScopedSession> onCompleted
    ) : IPluginProcessSession
    {
        private int _completionObserved;

        public int ProcessId => inner.ProcessId;
        public bool IsRunning => inner.IsRunning;
        public Task<ProcessExitOutcome> Completion => inner.Completion;

        public IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
            CancellationToken cancellationToken = default
        )
        {
            return inner.ReadOutputAsync(cancellationToken);
        }

        public ValueTask WriteStandardInputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        )
        {
            return inner.WriteStandardInputAsync(data, cancellationToken);
        }

        public ValueTask CompleteStandardInputAsync(
            CancellationToken cancellationToken = default
        )
        {
            return inner.CompleteStandardInputAsync(cancellationToken);
        }

        public void Terminate()
        {
            inner.Terminate();
        }

        public void Dispose()
        {
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }

        public void ObserveCompletion()
        {
            _ = ObserveCompletionAsync();
        }

        private async Task ObserveCompletionAsync()
        {
            try
            {
                await inner.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[Plugin:{pluginId}] Process session completion failed: {ex.Message}"
                );
            }
            finally
            {
                if (Interlocked.Exchange(ref _completionObserved, 1) == 0)
                {
                    onCompleted(this);
                }
            }
        }
    }
}
