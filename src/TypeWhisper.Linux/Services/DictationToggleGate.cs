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
    private bool _closed;
    private TaskCompletionSource? _closedAndIdle;
    private bool _gateDisposed;
    private bool _pendingStop;
    private bool _pendingStopWasCancel;
    private int _queuedWaiterCount;
    private bool _startupInProgress;

    internal int CurrentCount => _gate.CurrentCount;

    internal bool IsClosed
    {
        get
        {
            lock (_coordinationLock)
            {
                return _closed;
            }
        }
    }

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
            if (!TryAcquireLocked())
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
                ReleaseLocked();
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
            ReleaseLocked();

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
            if (TryAcquireLocked())
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
            if (_closed)
            {
                return DictationStopGateResult.Busy;
            }

            // Cancel intent is sticky so a later ordinary stop can't downgrade a queued discard to a save.
            _pendingStopWasCancel |= wasCancel;
            _pendingStop = true;
            if (!TryAcquireLocked())
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
    internal async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task<bool> waitTask;
        lock (_coordinationLock)
        {
            if (_closed)
            {
                return false;
            }

            // Register the transition while CloseAsync is excluded. This covers both a queued
            // waiter and a synchronously reserved permit until ownership is installed below.
            waitTask = _gate.WaitAsync(timeout);
            _queuedWaiterCount++;
        }

        var acquired = await waitTask.ConfigureAwait(false);
        lock (_coordinationLock)
        {
            _queuedWaiterCount--;
            if (!acquired)
            {
                CompleteCloseIfIdleLocked();
                return false;
            }

            if (_closed)
            {
                _gate.Release();
                CompleteCloseIfIdleLocked();
                return false;
            }

            return true;
        }
    }

    internal bool TryAcquire()
    {
        lock (_coordinationLock)
        {
            return TryAcquireLocked();
        }
    }

    internal void Release()
    {
        lock (_coordinationLock)
        {
            ReleaseLocked();
        }
    }

    internal Task<bool> CloseAsync(TimeSpan budget)
    {
        Task closedAndIdle;
        lock (_coordinationLock)
        {
            _closed = true;
            if (IsIdleLocked())
            {
                return Task.FromResult(true);
            }

            _closedAndIdle ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            closedAndIdle = _closedAndIdle.Task;
        }

        return WaitForClosedAndIdleAsync(closedAndIdle, budget);
    }

    public void Dispose()
    {
        lock (_coordinationLock)
        {
            if (_gateDisposed)
            {
                return;
            }

            _closed = true;
            if (!IsIdleLocked())
            {
                return;
            }

            _gate.Dispose();
            _gateDisposed = true;
        }
    }

    private bool TryAcquireLocked()
    {
        // Keep the closed check and semaphore acquisition in this same critical section. Otherwise
        // CloseAsync could close an apparently idle gate while a pre-checked caller acquires it.
        if (_closed || !_gate.Wait(0))
        {
            return false;
        }

        return true;
    }

    private void ReleaseLocked()
    {
        _gate.Release();
        CompleteCloseIfIdleLocked();
    }

    private void CompleteCloseIfIdleLocked()
    {
        if (_closed && IsIdleLocked())
        {
            _closedAndIdle?.TrySetResult();
        }
    }

    private bool IsIdleLocked()
    {
        return _gate.CurrentCount == 1 && _queuedWaiterCount == 0;
    }

    private static async Task<bool> WaitForClosedAndIdleAsync(
        Task closedAndIdle,
        TimeSpan budget
    )
    {
        try
        {
            await closedAndIdle.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
