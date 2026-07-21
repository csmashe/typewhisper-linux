namespace TypeWhisper.Linux.Services;

internal enum DictationStopGateResult
{
    Acquired,
    PendingStartupCompletion,
    Busy,
}

/// <summary>
///     A stop that startup must honor after it releases the gate, plus whether that stop was a
///     cancel (discard).
/// </summary>
internal readonly record struct DictationDeferredStop(bool HasPendingStop, bool WasCancel);

/// <summary>
///     Coordinates start/stop ownership of the dictation toggle gate. A stop that loses the
///     non-blocking gate race to startup is remembered; other gate contention keeps the existing
///     bail-out behavior.
/// </summary>
internal sealed class DictationToggleGate : IDisposable
{
    private readonly Lock _coordinationLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _pendingStop;
    private bool _pendingStopWasCancel;
    private bool _startupInProgress;

    internal int CurrentCount => _gate.CurrentCount;

    internal bool HasPendingStop
    {
        get
        {
            lock (_coordinationLock)
            {
                return _pendingStop;
            }
        }
    }

    /// <summary>
    ///     Attempts to acquire the gate for startup. Initialization runs while the coordination
    ///     state is locked, immediately before startup becomes observable to stop callers.
    /// </summary>
    internal bool TryBeginStartup(Action initializeStartup)
    {
        lock (_coordinationLock)
        {
            if (!_gate.Wait(0))
            {
                return false;
            }

            // A throwing initializer must not leave the gate acquired forever.
            try
            {
                _pendingStop = false;
                _pendingStopWasCancel = false;
                initializeStartup();
                _startupInProgress = true;
                return true;
            }
            catch
            {
                _startupInProgress = false;
                _gate.Release();
                throw;
            }
        }
    }

    /// <summary>
    ///     Clears the startup marker before releasing the gate, then consumes and reports any stop
    ///     that startup must honor. The pending check is serialized with stop's retry so exactly one
    ///     side takes responsibility for the stop.
    /// </summary>
    internal DictationDeferredStop CompleteStartupAndRelease()
    {
        lock (_coordinationLock)
        {
            _startupInProgress = false;
            _gate.Release();

            var deferred = new DictationDeferredStop(_pendingStop, _pendingStopWasCancel);
            _pendingStop = false;
            _pendingStopWasCancel = false;
            return deferred;
        }
    }

    /// <summary>
    ///     Attempts the stop protocol: probe once, remember a stop only for startup contention, and
    ///     retry once in case startup released after the failed probe. The optional callback is a
    ///     deterministic concurrency seam for the retry-race unit test.
    /// </summary>
    internal DictationStopGateResult TryAcquireForStop(
        bool wasCancel = false,
        Action? beforeRememberingPendingStop = null)
    {
        lock (_coordinationLock)
        {
            if (_gate.Wait(0))
            {
                _pendingStop = false;
                _pendingStopWasCancel = false;
                return DictationStopGateResult.Acquired;
            }

            if (!_startupInProgress)
            {
                return DictationStopGateResult.Busy;
            }
        }

        // Startup can complete here. Remember the request based on the startup state observed with
        // the failed probe, then retry while holding the coordination lock so a new start cannot
        // overtake the retry and clear the just-written request as stale.
        beforeRememberingPendingStop?.Invoke();

        lock (_coordinationLock)
        {
            // Cancel intent is sticky so a later ordinary stop can't downgrade a queued discard to a save.
            _pendingStopWasCancel |= wasCancel;
            _pendingStop = true;
            if (!_gate.Wait(0))
            {
                return DictationStopGateResult.PendingStartupCompletion;
            }

            _pendingStop = false;
            _pendingStopWasCancel = false;
            return DictationStopGateResult.Acquired;
        }
    }

    /// <summary>
    ///     Bounded acquisition used by session-loss teardown. Its waiting semantics intentionally
    ///     remain independent of the start/stop pending-request protocol.
    /// </summary>
    internal Task<bool> WaitAsync(TimeSpan timeout)
    {
        return _gate.WaitAsync(timeout);
    }

    internal bool TryAcquire()
    {
        lock (_coordinationLock)
        {
            return _gate.Wait(0);
        }
    }

    internal void Release()
    {
        lock (_coordinationLock)
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
