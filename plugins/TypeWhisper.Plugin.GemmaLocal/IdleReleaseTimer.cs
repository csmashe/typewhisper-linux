namespace TypeWhisper.Plugin.GemmaLocal;

/// <summary>
///     Releases a resident model once it has stayed idle for the host's delay. <see cref="Touch" />
///     after each use restarts the wait; a release that finds its owner busy is retried shortly.
///     The delay is checked again at least every <see cref="s_recheckInterval" />, so a changed
///     setting also applies to a model that is already idle.
/// </summary>
/// <remarks>
///     The release callback receives a check that is true only while no later <see cref="Touch" />
///     or <see cref="Cancel" /> happened. The owner calls it after taking the lock its uses hold
///     and touch under, so a model loaded or used since the timer fired is never released.
/// </remarks>
internal sealed class IdleReleaseTimer(
    TimeProvider time,
    Func<TimeSpan?> delay,
    Func<Func<bool>, bool> tryRelease
) : IDisposable
{
    internal static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan s_recheckInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long _version;
    private long _lastUse;
    private bool _disposed;

    internal bool IsArmed
    {
        get
        {
            lock (_gate)
                return _timer is not null;
        }
    }

    /// <summary>Records a use of the resident model and restarts its idle wait.</summary>
    public void Touch()
    {
        var due = delay();
        lock (_gate)
        {
            _lastUse = time.GetTimestamp();
            Arm(UntilNextCheck(due));
        }
    }

    /// <summary>Stops waiting, for example after the model was unloaded explicitly.</summary>
    public void Cancel()
    {
        lock (_gate)
            Disarm();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Disarm();
        }
    }

    private static TimeSpan UntilNextCheck(TimeSpan? remaining) =>
        remaining is { } due && due < s_recheckInterval ? due : s_recheckInterval;

    private void Arm(TimeSpan due)
    {
        Disarm();
        if (_disposed)
            return;

        var version = _version;
        _timer = time.CreateTimer(_ => Fire(version), null, due, Timeout.InfiniteTimeSpan);
    }

    // A new version retires callbacks of earlier timers that are already running.
    private void Disarm()
    {
        _version++;
        _timer?.Dispose();
        _timer = null;
    }

    private bool IsCurrent(long version)
    {
        lock (_gate)
            return !_disposed && version == _version;
    }

    private void Fire(long version)
    {
        long lastUse;
        lock (_gate)
        {
            if (_disposed || version != _version)
                return;

            lastUse = _lastUse;
        }

        TimeSpan? next;
        try
        {
            // The host setting may have changed since the wait began.
            if (delay() is not { } current)
                next = s_recheckInterval;
            else if (current - time.GetElapsedTime(lastUse) is var remaining && remaining > TimeSpan.Zero)
                next = UntilNextCheck(remaining);
            else
                next = tryRelease(() => IsCurrent(version)) ? null : s_retryDelay;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The owner logs its own failures; retrying a throwing release would only repeat it.
            next = null;
        }

        lock (_gate)
        {
            if (_disposed || version != _version)
                return;

            if (next is { } due)
                Arm(due);
            else
                Disarm();
        }
    }
}
