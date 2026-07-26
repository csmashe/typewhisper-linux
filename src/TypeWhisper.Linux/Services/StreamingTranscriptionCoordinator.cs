using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Owns the lifetime of a single <see cref="IStreamingSession" />: connects via
///     <see cref="ITranscriptionEngineRole.StartStreamingAsync" />, accepts live PCM
///     audio frames from the audio tap, drives the session's sender on a single reader
///     task, and exposes the joined final-segment text on <see cref="FinalizeAsync" />.
///     Mirrors upstream Windows <c>StreamingHandler.cs</c>'s A9/A10 concurrency
///     contract: bounded channel with <c>DropOldest</c>, <c>TryWrite</c> only from the
///     audio thread, strictly sequential <c>SendAudioAsync</c> from the reader task.
///     On any socket fault the coordinator transitions to <see cref="Faulted" /> and
///     fires the <c>onFault</c> callback — it does NOT itself fall back to batch.
/// </summary>
internal sealed class StreamingTranscriptionCoordinator : IAsyncDisposable
{
    private const int ChannelCapacity = 128;
    private const int MaxPendingBytes = 1024 * 1024;
    private const int FinalizeSenderTimeoutMs = 2000;
    private const int FinalizeSessionTimeoutMs = 2000;

    private const int FinalizeGraceWindowMs = 500;

    // Debounce: after a late final arrives in the grace window, wait this long
    // for additional finals before returning. Providers can flush multiple final
    // segments at EOF; the first one shouldn't short-circuit the rest.
    private const int FinalizeGraceQuietMs = 150;

    // Poll cadence for the grace-window debounce loop.
    private const int FinalizeGracePollMs = 25;

    private readonly StringBuilder _finalSegments = new();
    private readonly TimeSpan _finalizeSenderTimeout;
    private readonly TimeSpan _finalizeSessionTimeout;
    private readonly string? _language;

    private readonly Lock _lock = new();
    private readonly Action<Exception> _onFault;
    private readonly Action<int, string> _onPartial;
    private readonly Queue<byte[]> _pending = new();

    private readonly ITranscriptionEngineRole _plugin;
    private readonly int _sessionVersion;
    private Channel<byte[]>? _channel;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private bool _finalizing;

    // Once a finalize deadline expires, DisposeAsync must not issue another
    // session FinalizeAsync: a sender that ignored cancellation may still be
    // inside SendAudioAsync, and concurrent send/finalize violates the
    // streaming-session contract.
    private bool _skipSessionFinalize;

    // TickCount64 of the last late final received; 0 if none yet. Used by the
    // FinalizeAsync grace-window debounce so multi-final EOF flushes are caught.
    private long _lastFinalTickMs;
    private bool _open;
    private int _pendingBytes;
    private Task? _senderTask;

    private IStreamingSession? _session;
    private Action<StreamingTranscriptEvent>? _transcriptHandler;

    public StreamingTranscriptionCoordinator(
        ITranscriptionEngineRole plugin,
        string? language,
        int sessionVersion,
        Action<int, string> onPartial,
        Action<Exception> onFault,
        TimeSpan? finalizeSenderTimeout = null,
        TimeSpan? finalizeSessionTimeout = null)
    {
        _plugin = plugin;
        _language = language;
        _sessionVersion = sessionVersion;
        _onPartial = onPartial;
        _onFault = onFault;
        _finalizeSenderTimeout = finalizeSenderTimeout
                                 ?? TimeSpan.FromMilliseconds(FinalizeSenderTimeoutMs);
        _finalizeSessionTimeout = finalizeSessionTimeout
                                  ?? TimeSpan.FromMilliseconds(FinalizeSessionTimeoutMs);
    }

    public bool Faulted { get; private set; }

    // ReSharper disable once UnusedMember.Global  public API surface (final-text availability flag); not currently called in-tree
    public bool HasFinalText
    {
        get
        {
            lock (_lock)
            {
                return _finalSegments.Length > 0;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
            }
        }
        catch
        {
            /* ignore */
        }

        IStreamingSession? session;
        Channel<byte[]>? channel;
        Action<StreamingTranscriptEvent>? handler;
        Task? senderTask;
        lock (_lock)
        {
            session = _session;
            channel = _channel;
            handler = _transcriptHandler;
            senderTask = _senderTask;
            _session = null;
            _channel = null;
            _transcriptHandler = null;
            _senderTask = null;
            _open = false;
            _pending.Clear();
            _pendingBytes = 0;
        }

        channel?.Writer.TryComplete();
        if (session is not null && handler is not null)
        {
            session.TranscriptReceived -= handler;
        }

        // Drain the sender before graceful cleanup; if it will not drain, skip
        // session FinalizeAsync and dispose directly. When FinalizeAsync already
        // timed out this sender, re-waiting would burn another full deadline
        // before the batch fallback — short-circuit.
        var skipSessionFinalize = Volatile.Read(ref _skipSessionFinalize);
        var senderDrained = senderTask is null;
        if (senderTask is not null && !skipSessionFinalize)
        {
            try
            {
                await senderTask.WaitAsync(_finalizeSenderTimeout);
                senderDrained = true;
            }
            catch
            {
                senderDrained = senderTask.IsCompleted;
            }
        }

        if (session is not null)
        {
            if (skipSessionFinalize || !senderDrained)
            {
                await DisposeSessionAsync(session, _finalizeSessionTimeout);
            }
            else
            {
                await CleanupSessionAsync(session, _finalizeSessionTimeout);
            }
        }

        try { _cts?.Dispose(); }
        catch
        {
            /* ignore */
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var lang = _language == "auto" ? null : _language;
            var session = await _plugin.StartStreamingAsync(lang, _cts.Token);

            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
            });

            var handler = OnTranscriptReceived;

            // Publish everything atomically (sender task, channel, session, pending flush)
            // so FinalizeAsync never sees _open=true with _senderTask still null or the
            // pending queue undrained. Re-check _disposed/_finalizing: if those ran during
            // the connect await, we must not publish — those callers won't come back to
            // clean up the session. Tear it down locally instead.
            var published = false;
            lock (_lock)
            {
                if (!_disposed && !_finalizing)
                {
                    session.TranscriptReceived += handler;
                    _session = session;
                    _channel = channel;
                    _transcriptHandler = handler;
                    _senderTask = RunSenderAsync(session, channel.Reader, _cts.Token);
                    FlushPendingIntoChannel(channel.Writer);
                    _open = true;
                    published = true;
                }
            }

            if (!published)
            {
                await CleanupSessionAsync(session, _finalizeSessionTimeout);
            }
        }
        catch (OperationCanceledException) when (_disposed || _finalizing || ct.IsCancellationRequested)
        {
            // Normal teardown (Dispose, Finalize, or caller cancel) — not a fault.
        }
        catch (Exception ex)
        {
            HandleFault(ex);
        }
    }

    public void AcceptAudioFrame(float[] samples, int sampleRate)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- public entry point fed by external capture callers; keep the defensive null guard despite the non-nullable annotation
        if (_disposed || Faulted || samples is null || samples.Length == 0)
        {
            return;
        }

        var sixteen = sampleRate != 16000
            ? AudioRecordingService.ResampleToSampleRate(samples, sampleRate, 16000)
            : samples;

        var pcm16 = new byte[sixteen.Length * 2];
        for (var i = 0; i < sixteen.Length; i++)
        {
            var s = AudioRecordingService.ToPcm16(sixteen[i]);
            pcm16[i * 2] = (byte)(s & 0xFF);
            pcm16[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        Channel<byte[]>? channel;
        lock (_lock)
        {
            channel = _open ? _channel : null;
            if (channel is null)
            {
                _pending.Enqueue(pcm16);
                _pendingBytes += pcm16.Length;
                while (_pendingBytes > MaxPendingBytes && _pending.TryDequeue(out var dropped))
                {
                    _pendingBytes -= dropped.Length;
                }

                return;
            }
        }

        channel.Writer.TryWrite(pcm16);
    }

    public async Task<string> FinalizeAsync(CancellationToken ct)
    {
        if (Faulted)
        {
            return SnapshotFinalSegments();
        }

        Channel<byte[]>? channel;
        IStreamingSession? session;
        Task? senderTask;
        lock (_lock)
        {
            // Set _finalizing under lock so StartAsync's publish guard sees it atomically.
            // Any session that connects after this will be torn down by StartAsync, not published.
            _finalizing = true;
            channel = _channel;
            session = _session;
            senderTask = _senderTask;
        }

        // Pre-publish path: cancel _cts so a well-behaved plugin exits its connect.
        if (session is null)
        {
            try
            {
                if (_cts is not null)
                {
                    await _cts.CancelAsync();
                }
            }
            catch
            {
                /* ignore */
            }
        }

        channel?.Writer.TryComplete();

        // ct is the soft "give up sooner" signal; timeouts are hard upper bounds for misbehaving plugins.
        var senderDrainTimedOut = false;
        if (senderTask is not null)
        {
            try
            {
                await senderTask.WaitAsync(_finalizeSenderTimeout, ct);
            }
            catch (TimeoutException)
            {
                senderDrainTimedOut = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller abandoned the dictation — keep the non-fault result
                // contract, but do not finalize while the sender may be in flight.
                return SnapshotFinalSegments();
            }
        }

        // If the sender faulted, HandleFault already owns session teardown — bail before
        // our own FinalizeAsync to avoid concurrent finalize/dispose on the same instance.
        if (Faulted)
        {
            return SnapshotFinalSegments();
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement
        // These are independent short-circuit guards over different variables
        // (Faulted, drain-timeout+cancel, drain-timeout), not one switchable expression.
        if (senderDrainTimedOut && ct.IsCancellationRequested)
        {
            return SnapshotFinalSegments();
        }

        if (senderDrainTimedOut)
        {
            Volatile.Write(ref _skipSessionFinalize, true);
            Trace.WriteLine(
                $"[StreamingCoordinator] Sender-drain deadline exhausted after "
                + $"{_finalizeSenderTimeout.TotalMilliseconds:0} ms; streaming result is ineligible."
            );
            throw new TimeoutException(
                $"Streaming sender-drain deadline exhausted after "
                + $"{_finalizeSenderTimeout.TotalMilliseconds:0} ms."
            );
        }

        Exception? sessionFinalizeFault = null;
        TimeoutException? sessionFinalizeTimeout = null;

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        // Kept beside the sessionFinalizeFault/sessionFinalizeTimeout state it mutates,
        // ahead of the finalize block that calls it; moving it would split the group.
        void RecordSessionFinalizeTimeout(Exception? innerException = null)
        {
            Volatile.Write(ref _skipSessionFinalize, true);
            sessionFinalizeTimeout = new TimeoutException(
                $"Streaming session-finalize deadline exhausted after "
                + $"{_finalizeSessionTimeout.TotalMilliseconds:0} ms.",
                innerException
            );
            Trace.WriteLine(
                $"[StreamingCoordinator] Session-finalize deadline exhausted after "
                + $"{_finalizeSessionTimeout.TotalMilliseconds:0} ms; "
                + "streaming result is ineligible."
            );
        }

        if (session is not null)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? sessionFinalizeTask = null;
            try
            {
                // Call inside the try so a plugin that throws synchronously from
                // FinalizeAsync is still captured as a provider fault below.
                sessionFinalizeTask = session.FinalizeAsync(sessionCts.Token);

                // Hard deadline: even a plugin that ignores cancellation cannot
                // block teardown. deadlineTask is the SOLE deadline source and
                // drives sessionCts — a separate CancelAfter timer could cancel
                // (and complete) a cancellation-honoring plugin an instant before
                // deadlineTask elapses, making it win the race indistinguishably
                // from on-time success. With one source the WhenAny winner is
                // authoritative.
                var deadlineTask = Task.Delay(_finalizeSessionTimeout, CancellationToken.None);
                var completedTask = await Task.WhenAny(sessionFinalizeTask, deadlineTask)
                    .WaitAsync(ct);

                if (completedTask == deadlineTask)
                {
                    try { await sessionCts.CancelAsync(); }
                    catch
                    {
                        /* best effort */
                    }

                    ObserveLateFault(sessionFinalizeTask);

                    if (ct.IsCancellationRequested)
                    {
                        // Caller abandonment stays non-fault, but the abandoned
                        // finalize means disposal must not issue a second
                        // FinalizeAsync on this session.
                        Volatile.Write(ref _skipSessionFinalize, true);
                        Trace.WriteLine(
                            "[StreamingCoordinator] FinalizeAsync session canceled by caller."
                        );
                    }
                    else
                    {
                        RecordSessionFinalizeTimeout();
                    }
                }
                else
                {
                    // Finalize won the race — await it so a provider-thrown fault
                    // stays classified as a provider fault.
                    await sessionFinalizeTask;
                }
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                // Caller abandonment via .WaitAsync(ct) while the plugin ignores its
                // token: the finalize task is still running, so mark it abandoned
                // and observe any late fault.
                Volatile.Write(ref _skipSessionFinalize, true);
                ObserveLateFault(sessionFinalizeTask);
                Trace.WriteLine(
                    $"[StreamingCoordinator] FinalizeAsync session canceled: {ex.Message}"
                );
            }
            catch (OperationCanceledException ex)
            {
                // Deadline cancellation is handled on the deadline-win branch; an
                // OCE here is the provider surfacing its own cancellation — a
                // provider fault.
                Trace.WriteLine(
                    $"[StreamingCoordinator] FinalizeAsync session fault: {ex.Message}"
                );
                sessionFinalizeFault = ex;
            }
            catch (Exception ex)
            {
                // Capture and rethrow after the grace window so late finals still land, but
                // surface to the caller so DictationOrchestrator's finalizeThrew triggers
                // batch fallback. Without this, a partial transcript is silently treated as success.
                Trace.WriteLine($"[StreamingCoordinator] FinalizeAsync session fault: {ex.Message}");
                sessionFinalizeFault = ex;
            }
        }

        // Grace window: wait for FinalizeGraceQuietMs of silence after the latest final so
        // providers that flush multiple segments at EOF all land before we return. Hard cap at
        // FinalizeGraceWindowMs; exits immediately on caller cancel.
        var graceDeadline = Environment.TickCount64 + FinalizeGraceWindowMs;
        while (Environment.TickCount64 < graceDeadline)
        {
            var lastFinal = Volatile.Read(ref _lastFinalTickMs);
            if (lastFinal > 0 && Environment.TickCount64 - lastFinal >= FinalizeGraceQuietMs)
            {
                break;
            }

            try { await Task.Delay(FinalizeGracePollMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        if (sessionFinalizeTimeout is not null)
        {
            throw sessionFinalizeTimeout;
        }

        if (sessionFinalizeFault is not null)
        {
            throw new InvalidOperationException(
                $"Streaming session faulted during finalize: {sessionFinalizeFault.Message}",
                sessionFinalizeFault);
        }

        return SnapshotFinalSegments();
    }

    // StringBuilder is not thread-safe. OnTranscriptReceived appends under _lock,
    // so reads must also take _lock — otherwise a concurrent final-segment append
    // during the grace window can produce a torn or partial transcript.
    private string SnapshotFinalSegments()
    {
        lock (_lock)
        {
            return _finalSegments.ToString().Trim();
        }
    }

    private async Task RunSenderAsync(
        IStreamingSession session,
        ChannelReader<byte[]> reader,
        CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                await session.SendAudioAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown — Finalize or Dispose closed the writer.
        }
        catch (Exception ex)
        {
            // Plugin SendAudioAsync is external and can throw arbitrary types;
            // route all non-cancel failures through HandleFault so batch fallback fires.
            HandleFault(ex);
        }
    }

    private void OnTranscriptReceived(StreamingTranscriptEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _onPartial(_sessionVersion, evt.Text);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[StreamingCoordinator] onPartial callback threw: {ex.Message}");
        }

        if (!evt.IsFinal || string.IsNullOrWhiteSpace(evt.Text))
        {
            return;
        }

        lock (_lock)
        {
            if (_finalSegments.Length > 0)
            {
                _finalSegments.Append('\n');
            }

            _finalSegments.Append(evt.Text.Trim());
        }

        Volatile.Write(ref _lastFinalTickMs, Environment.TickCount64);
    }

    private void HandleFault(Exception ex)
    {
        // DELIBERATE FORK DIVERGENCE from upstream CleanupStreamingSessionAfterFailure
        // (StreamingHandler.cs:290): upstream leaves the user with the partial transcript.
        // The Linux fork's DictationOrchestrator reads `Faulted` and falls back to batch
        // TranscribeAsync(wav) — lossless because the audio tap is non-destructive.
        // Do NOT add batch-fallback logic here; the coordinator must not know about the WAV.
        // Keep the divergence in the caller so a future upstream StreamingHandler sync
        // doesn't need to re-argue this decision.
        IStreamingSession? session;
        Channel<byte[]>? channel;
        Action<StreamingTranscriptEvent>? handler;
        lock (_lock)
        {
            if (Faulted)
            {
                return;
            }

            Faulted = true;
            session = _session;
            channel = _channel;
            handler = _transcriptHandler;
            _session = null;
            _channel = null;
            _transcriptHandler = null;
            _pending.Clear();
            _pendingBytes = 0;
            _open = false;
        }

        Trace.WriteLine($"[StreamingCoordinator] Fault: {ex.GetType().Name}: {ex.Message}");

        channel?.Writer.TryComplete();
        if (session is not null && handler is not null)
        {
            session.TranscriptReceived -= handler;
        }

        try { _onFault(ex); }
        catch (Exception cbEx)
        {
            Trace.WriteLine($"[StreamingCoordinator] onFault callback threw: {cbEx.Message}");
        }

        if (session is not null)
        {
            _ = CleanupSessionAsync(session, _finalizeSessionTimeout);
        }
    }

    private static async Task CleanupSessionAsync(
        IStreamingSession session,
        TimeSpan finalizeTimeout)
    {
        using var cts = new CancellationTokenSource(finalizeTimeout);
        try { await session.FinalizeAsync(cts.Token); }
        catch
        {
            /* best effort */
        }

        await DisposeSessionAsync(session, finalizeTimeout);
    }

    // Fault-only continuation for an abandoned task so a late exception cannot
    // surface as an unobserved task exception.
    private static void ObserveLateFault(Task? task) =>
        _ = task?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private static async Task DisposeSessionAsync(
        IStreamingSession session,
        TimeSpan disposeTimeout)
    {
        Task disposeTask;
        try
        {
            disposeTask = session.DisposeAsync().AsTask();
        }
        catch
        {
            /* synchronous throw from DisposeAsync — nothing left to await */
            return;
        }

        try
        {
            // Bound disposal too: a provider blocked in DisposeAsync (or still
            // ignoring cancellation in SendAudioAsync) must not stall the
            // complete-WAV batch fallback the caller runs next.
            await disposeTask.WaitAsync(disposeTimeout);
        }
        catch (TimeoutException)
        {
            Trace.WriteLine(
                $"[StreamingCoordinator] Session DisposeAsync exceeded "
                + $"{disposeTimeout.TotalMilliseconds:0} ms; detaching from teardown."
            );
            ObserveLateFault(disposeTask);
        }
        catch
        {
            /* best effort */
        }
    }

    private void FlushPendingIntoChannel(ChannelWriter<byte[]> writer)
    {
        while (true)
        {
            byte[]? next;
            lock (_lock)
            {
                if (!_pending.TryDequeue(out next))
                {
                    _pendingBytes = 0;
                    return;
                }

                _pendingBytes -= next.Length;
            }

            if (!writer.TryWrite(next))
            {
                Trace.WriteLine("[StreamingCoordinator] FlushPending TryWrite returned false");
            }
        }
    }
}
