namespace TypeWhisper.Linux.Services;

/// <summary>
///     Tracks dictation session ids that are recording or in their post-stop
///     pipeline (save/transcribe/insert). <see cref="RunAsync" /> is the single
///     chokepoint every post-stop pipeline must run through: it guarantees the
///     session id is removed on every exit of the <c>pipeline</c> — success,
///     cancellation, or an exception from any step, even one (e.g. persisting
///     the capture) that throws before later steps ever start.
/// </summary>
internal sealed class DictationInFlightSessionTracker
{
    private readonly Lock _lock = new();
    private readonly HashSet<int> _sessions = [];

    private bool _starting;
    private bool _recovering;

    internal bool TryBeginStartup()
    {
        lock (_lock)
        {
            if (_recovering)
                return false;
            _starting = true;
            return true;
        }
    }

    internal void EndStartup()
    {
        lock (_lock)
            _starting = false;
    }

    internal bool TryBeginRecovery()
    {
        lock (_lock)
        {
            if (_starting || _recovering || _sessions.Count > 0)
                return false;
            _recovering = true;
            return true;
        }
    }

    internal void EndRecovery()
    {
        lock (_lock)
            _recovering = false;
    }

    internal void Begin(int sessionId)
    {
        lock (_lock)
        {
            _sessions.Add(sessionId);
        }
    }

    /// <summary>Idempotent: removing an id that is not tracked is a no-op.</summary>
    internal void End(int sessionId)
    {
        lock (_lock)
        {
            _sessions.Remove(sessionId);
        }
    }

    internal bool Contains(int sessionId)
    {
        lock (_lock)
        {
            return _sessions.Contains(sessionId);
        }
    }

    internal async Task RunAsync(int sessionId, Func<Task> pipeline)
    {
        try
        {
            await pipeline().ConfigureAwait(false);
        }
        finally
        {
            End(sessionId);
        }
    }
}
