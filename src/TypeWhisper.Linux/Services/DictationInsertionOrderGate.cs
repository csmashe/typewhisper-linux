namespace TypeWhisper.Linux.Services;

/// <summary>
///     Orders concurrent post-stop dictation pipelines' text insertion so delivery
///     lands in session-start order even when a later session's transcription or
///     post-processing (including a slow prompt action) finishes first. Capture and
///     transcription stay fully concurrent — only the narrow span immediately around
///     the actual insertion call waits (audit §2 H3).
///
///     <see cref="Reserve" /> must run for every stopped session, in session-start
///     order, before that session's toggle-gate release — so a session that hasn't
///     even entered its own post-stop pipeline yet is still known to block a faster
///     successor. <see cref="WaitForTurnAsync" /> must run immediately before the
///     session inserts; <see cref="Release" /> must run immediately after (success or
///     failure) so a waiting successor isn't held up by unrelated post-insertion
///     bookkeeping. Release is idempotent and must also be called, unconditionally,
///     from a terminal safety net for every reserved session — mirroring
///     <see cref="DictationInFlightSessionTracker" />'s <c>RunAsync</c> finally — so a
///     session that fails/cancels/discards before ever reaching insertion can never
///     block a successor forever.
/// </summary>
internal sealed class DictationInsertionOrderGate
{
    private static readonly TimeSpan s_defaultMaxWait = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private readonly SortedSet<int> _pending = [];
    private readonly Dictionary<int, TaskCompletionSource> _waiters = new();
    private readonly TimeSpan _maxWait;

    internal DictationInsertionOrderGate()
        : this(s_defaultMaxWait)
    {
    }

    /// <summary>Test-only hook so the defensive backstop timeout can be exercised quickly.</summary>
    internal DictationInsertionOrderGate(TimeSpan maxWait)
    {
        _maxWait = maxWait;
    }

    internal void Reserve(int sessionId)
    {
        lock (_lock)
        {
            _pending.Add(sessionId);
        }
    }

    /// <summary>
    ///     Waits until every reserved session with a smaller id has released its slot.
    ///     Returns immediately if <paramref name="sessionId" /> is already the oldest
    ///     reservation (or nothing is reserved). The real correctness guarantee is that
    ///     <see cref="Release" /> is always eventually called for every reservation
    ///     (from a guaranteed terminal path); the <c>maxWait</c> backstop below only
    ///     protects against a future bug that forgets to release, and fails OPEN
    ///     (lets insertion proceed out of order) rather than hanging the pipeline.
    ///     A real cancellation of <paramref name="cancellationToken" /> instead
    ///     completes the wait as canceled, so the caller's existing
    ///     <c>OperationCanceledException</c> handling short-circuits before ever
    ///     attempting the insertion.
    /// </summary>
    internal async Task WaitForTurnAsync(int sessionId, CancellationToken cancellationToken)
    {
        // Before the fast path too: an already-canceled session that happens to be the
        // queue head would otherwise return normally and go on to insert.
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource tcs;
        lock (_lock)
        {
            if (_pending.Count == 0 || _pending.Min == sessionId)
            {
                return;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[sessionId] = tcs;
        }

        using var timeoutCts = new CancellationTokenSource(_maxWait);
        await using var timeoutReg = timeoutCts.Token.Register(() => tcs.TrySetResult());
        await using var cancelReg = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken)
        );
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Idempotent: releasing a session that is not (or no longer) reserved is a no-op.</summary>
    internal void Release(int sessionId)
    {
        lock (_lock)
        {
            if (!_pending.Remove(sessionId))
            {
                return;
            }

            // Clean up sessionId's own waiter entry too, in case its wait already
            // resolved via the timeout/cancel path above instead of being unblocked
            // by a predecessor's Release.
            _waiters.Remove(sessionId);

            if (_pending.Count > 0 && _waiters.Remove(_pending.Min, out var next))
            {
                next.TrySetResult();
            }
        }
    }
}
