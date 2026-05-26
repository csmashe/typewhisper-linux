using System.Diagnostics;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Owns the lifetime of a single <see cref="IStreamingSession" />: connects via
///     <see cref="ITranscriptionEnginePlugin.StartStreamingAsync" />, accepts live PCM
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

    private readonly ITranscriptionEnginePlugin _plugin;
    private readonly string? _language;
    private readonly int _sessionVersion;
    private readonly Action<int, string> _onPartial;
    private readonly Action<Exception> _onFault;

    private readonly object _lock = new();
    private readonly Queue<byte[]> _pending = new();
    private int _pendingBytes;
    private bool _open;
    private bool _disposed;
    private bool _finalizing;

    private IStreamingSession? _session;
    private Channel<byte[]>? _channel;
    private Task? _senderTask;
    private Action<StreamingTranscriptEvent>? _transcriptHandler;
    private CancellationTokenSource? _cts;

    private readonly System.Text.StringBuilder _finalSegments = new();
    // TickCount64 of the last late final received; 0 if none yet. Used by the
    // FinalizeAsync grace-window debounce so multi-final EOF flushes are caught.
    private long _lastFinalTickMs;

    public StreamingTranscriptionCoordinator(
        ITranscriptionEnginePlugin plugin,
        string? language,
        int sessionVersion,
        Action<int, string> onPartial,
        Action<Exception> onFault)
    {
        _plugin = plugin;
        _language = language;
        _sessionVersion = sessionVersion;
        _onPartial = onPartial;
        _onFault = onFault;
    }

    public bool Faulted { get; private set; }

    public bool HasFinalText
    {
        get
        {
            lock (_lock) return _finalSegments.Length > 0;
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
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            Action<StreamingTranscriptEvent> handler = OnTranscriptReceived;

            // Publish atomically: the sender task, channel, session, and the
            // pending-queue flush all become visible together. Without this,
            // FinalizeAsync could observe _open=true with _senderTask still null
            // (or with the pending queue undrained), complete the channel writer,
            // and call session.FinalizeAsync before any audio reached the wire.
            // FlushPendingIntoChannel re-enters _lock, which is reentrant.
            //
            // Also re-check _disposed and _finalizing under this lock: if
            // DisposeAsync or FinalizeAsync ran while we were awaiting the
            // connect and the plugin returned a session anyway (it ignored
            // cancellation, or resolved just before it), we must NOT publish —
            // those callers already snapshotted nulls and won't come back to
            // clean this session up. Tear it down locally instead.
            bool published = false;
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
                await CleanupSessionAsync(session);
                return;
            }
        }
        catch (OperationCanceledException) when (_disposed || _finalizing || ct.IsCancellationRequested)
        {
            // Normal teardown: DisposeAsync or FinalizeAsync cancelled _cts, or the
            // caller cancelled their own token during the connect handshake. Don't
            // surface as a fault — doing so would trigger spurious batch fallback
            // in Phase 4.
        }
        catch (Exception ex)
        {
            HandleFault(ex);
        }
    }

    public void AcceptAudioFrame(float[] samples, int sampleRate)
    {
        if (_disposed || Faulted || samples is null || samples.Length == 0) return;

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
        if (Faulted) return SnapshotFinalSegments();

        Channel<byte[]>? channel;
        IStreamingSession? session;
        Task? senderTask;
        lock (_lock)
        {
            // Set _finalizing under the lock so StartAsync's publish guard sees
            // it with proper memory ordering. After this point, any late-arriving
            // session from a pending connect will be torn down by StartAsync
            // instead of published — otherwise FinalizeAsync would return with
            // an empty transcript while a freshly-connected session kept running.
            _finalizing = true;
            channel = _channel;
            session = _session;
            senderTask = _senderTask;
        }

        // Pre-publish path: no sender to drain, no session to finalize. Cancel
        // _cts so a well-behaved plugin exits its connect; the publish guard
        // handles the misbehaving-plugin case.
        if (session is null)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
        }

        channel?.Writer.TryComplete();

        // Each phase honors the caller's ct so an aborted shutdown collapses the
        // up-to-4.5s worst-case wait. Timeouts stay as hard upper bounds for
        // misbehaving plugins; ct is the soft "give up sooner" signal.
        if (senderTask is not null)
        {
            try { await senderTask.WaitAsync(TimeSpan.FromMilliseconds(FinalizeSenderTimeoutMs), ct); }
            catch { /* best effort */ }
        }

        if (session is not null)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sessionCts.CancelAfter(FinalizeSessionTimeoutMs);
            try { await session.FinalizeAsync(sessionCts.Token); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[StreamingCoordinator] FinalizeAsync session: {ex.Message}");
            }
        }

        // Grace window — debounce: stay open for a brief quiet period after the
        // latest late final so providers flushing multiple final segments at EOF
        // all land in _finalSegments before we return. Hard-capped at
        // FinalizeGraceWindowMs total. Returns instantly via ct if the caller
        // cancels.
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

        return SnapshotFinalSegments();
    }

    // StringBuilder is not thread-safe. OnTranscriptReceived appends under _lock,
    // so reads must also take _lock — otherwise a concurrent final-segment append
    // during the grace window can produce a torn or partial transcript.
    private string SnapshotFinalSegments()
    {
        lock (_lock) return _finalSegments.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { /* ignore */ }

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
            session.TranscriptReceived -= handler;

        // Wait for the sender to exit before tearing down the session. Otherwise
        // CleanupSessionAsync would call FinalizeAsync/DisposeAsync on the
        // session while a plugin SendAudioAsync is still in flight — a violation
        // of the single-session-user contract that can race-close a websocket.
        if (senderTask is not null)
        {
            try { await senderTask.WaitAsync(TimeSpan.FromMilliseconds(FinalizeSenderTimeoutMs)); }
            catch { /* best effort — proceed with cleanup either way */ }
        }

        if (session is not null) await CleanupSessionAsync(session);

        try { _cts?.Dispose(); } catch { /* ignore */ }
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
            // Plugin SendAudioAsync implementations are external code and can
            // throw arbitrary types (HttpRequestException from a REST-style
            // streamer, plugin-internal exceptions, etc). Route ALL non-cancel
            // failures through HandleFault so Phase 4's batch fallback fires.
            HandleFault(ex);
        }
    }

    private void OnTranscriptReceived(StreamingTranscriptEvent evt)
    {
        if (_disposed) return;

        try
        {
            _onPartial(_sessionVersion, evt.Text);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[StreamingCoordinator] onPartial callback threw: {ex.Message}");
        }

        if (evt.IsFinal && !string.IsNullOrWhiteSpace(evt.Text))
        {
            lock (_lock)
            {
                if (_finalSegments.Length > 0) _finalSegments.Append('\n');
                _finalSegments.Append(evt.Text.Trim());
            }
            Volatile.Write(ref _lastFinalTickMs, Environment.TickCount64);
        }
    }

    private void HandleFault(Exception ex)
    {
        // DELIBERATE FORK DIVERGENCE from upstream's
        // CleanupStreamingSessionAfterFailure (StreamingHandler.cs:290): upstream
        // tears down and leaves the user with whatever transcript was accumulated
        // before the fault. The Linux fork's caller (DictationOrchestrator,
        // Phase 4) reads `Faulted` and falls back to batch TranscribeAsync(wav)
        // on the captured WAV from AudioRecordingService — lossless because the
        // audio tap is non-destructive, _sampleChunks keeps accumulating in
        // parallel with streaming. Do NOT add batch-fallback logic HERE — the
        // coordinator must not know about the WAV. Keep the divergence local
        // to the caller so a future upstream sync of StreamingHandler can be
        // pulled in without re-arguing this decision. See:
        //   - master plan: ~/.claude/plans/virtual-noodling-plum.md (Approach)
        //   - C5 Phase 3 doc: docs/plans/2026-05-25-c5-phase-3-coordinator.md
        IStreamingSession? session;
        Channel<byte[]>? channel;
        Action<StreamingTranscriptEvent>? handler;
        lock (_lock)
        {
            if (Faulted) return;
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
            session.TranscriptReceived -= handler;

        try { _onFault(ex); }
        catch (Exception cbEx)
        {
            Trace.WriteLine($"[StreamingCoordinator] onFault callback threw: {cbEx.Message}");
        }

        if (session is not null) _ = CleanupSessionAsync(session);
    }

    private static async Task CleanupSessionAsync(IStreamingSession session)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(FinalizeSessionTimeoutMs));
        try { await session.FinalizeAsync(cts.Token); } catch { /* best effort */ }
        try { await session.DisposeAsync(); }           catch { /* best effort */ }
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
