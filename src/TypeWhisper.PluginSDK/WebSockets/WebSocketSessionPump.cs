using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;

namespace TypeWhisper.PluginSDK.WebSockets;

public sealed class WebSocketSessionPump : IStreamingSession
{
    private readonly IWebSocketSessionAdapter _adapter;
    private readonly IWebSocketTransport _transport;
    private readonly WebSocketSessionPumpOptions _options;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _readyCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _finalizeSync = new();
    private readonly Lock _disposeSync = new();

    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private Task? _finalizeTask;
    private Task? _disposeTask;
    private Exception? _sessionFault;
    private int _state = (int)WebSocketSessionState.Created;
    private int _abortCalled;
    private int _transportDisposed;
    private int _sendStreamIndeterminate;

    private WebSocketSessionPump(
        IWebSocketSessionAdapter adapter,
        IWebSocketTransport transport,
        WebSocketSessionPumpOptions options
    )
    {
        _adapter = adapter;
        _transport = transport;
        _options = options;
    }

    // ReSharper disable once MemberCanBePrivate.Global -- the pump's observable lifecycle
    // state is public SDK surface (and the only thing that exposes WebSocketSessionState);
    // in-tree callers happen to be internal to this file.
    public WebSocketSessionState State =>
        (WebSocketSessionState)Volatile.Read(ref _state);

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<WebSocketSessionPump> ConnectAsync(
        IWebSocketSessionAdapter adapter,
        CancellationToken ct,
        WebSocketSessionPumpOptions? options = null,
        IWebSocketTransportFactory? transportFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(adapter);
        // Validate before Create so a bad option can't strand a transport nothing owns yet.
        var validated = ValidateOptions(options);
        var transport = (transportFactory ?? ClientWebSocketTransportFactory.Instance).Create();
        var pump = new WebSocketSessionPump(adapter, transport, validated);

        Volatile.Write(ref pump._state, (int)WebSocketSessionState.Connecting);
        try
        {
            var connectionOptions = await adapter.GetConnectionOptionsAsync(ct);
            await transport.ConnectAsync(connectionOptions, ct);
            await pump.StartAsync(ct);
            return pump;
        }
        catch
        {
            pump.AbortOnce();
            await pump.DisposeAsync();
            throw;
        }
    }

    public static async Task<WebSocketSessionPump> StartConnectedAsync(
        IWebSocketSessionAdapter adapter,
        IWebSocketTransport connectedTransport,
        CancellationToken ct,
        WebSocketSessionPumpOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(connectedTransport);
        if (connectedTransport.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(
                "The supplied WebSocket transport is not connected."
            );
        }

        var pump = new WebSocketSessionPump(
            adapter,
            connectedTransport,
            ValidateOptions(options)
        );
        try
        {
            await pump.StartAsync(ct);
            return pump;
        }
        catch
        {
            pump.AbortOnce();
            await pump.DisposeAsync();
            throw;
        }
    }

    public async Task SendAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            ThrowIfFaulted();
            ThrowIfSendStreamIndeterminate("send audio");
            EnsureState(WebSocketSessionState.Active, "send audio");
            IReadOnlyList<WebSocketOutboundMessage> messages;
            try
            {
                messages = await _adapter.EncodeAudioAsync(pcm16Audio, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CaptureFault(ex);
                ThrowIfFaulted();
                throw;
            }

            await SendBatchUnderGateAsync(messages, "audio", ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task FinalizeAsync(CancellationToken ct)
    {
        Task finalizeTask;
        lock (_finalizeSync)
        {
            // Uncancellable so one caller's token can't poison the shared task, and
            // a fault surfaces as the captured fault, not a raced cancellation.
            // CaptureFault or BeginDisposal always resolves both completions, so
            // this can't hang; WaitAsync below still honors each caller's token.
            _finalizeTask ??= FinalizeCoreAsync(CancellationToken.None);
            finalizeTask = _finalizeTask;
        }

        return finalizeTask.WaitAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                BeginDisposal();
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private static WebSocketSessionPumpOptions ValidateOptions(
        WebSocketSessionPumpOptions? options
    )
    {
        options ??= new WebSocketSessionPumpOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReceiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumMessageSize);
        return options;
    }

    private async Task StartAsync(CancellationToken ct)
    {
        Volatile.Write(ref _state, (int)WebSocketSessionState.Starting);
        // Published volatile: a dispose racing startup reads these in BackgroundTasks, and a
        // stale null there would tear the transport down without awaiting the loop.
        Volatile.Write(ref _receiveTask, ReceiveLoopAsync(_lifetimeCts.Token));

        await _sendGate.WaitAsync(ct);
        try
        {
            var startupMessages = await _adapter.OnConnectedAsync(ct);
            await SendBatchUnderGateAsync(startupMessages, "startup", ct);
        }
        finally
        {
            _sendGate.Release();
        }

        if (!_adapter.Readiness.Required)
            _readyCompletion.TrySetResult();

        await _readyCompletion.Task.WaitAsync(ct);
        ThrowIfFaulted();
        Volatile.Write(ref _state, (int)WebSocketSessionState.Active);

        if (_adapter.KeepAlive is not null)
        {
            Volatile.Write(
                ref _keepAliveTask,
                KeepAliveLoopAsync(_adapter.KeepAlive, _lifetimeCts.Token)
            );
        }
    }

    private async Task FinalizeCoreAsync(CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            ThrowIfFaulted();
            ThrowIfSendStreamIndeterminate("finalize");
            EnsureState(WebSocketSessionState.Active, "finalize");
            Volatile.Write(ref _state, (int)WebSocketSessionState.Finalizing);

            WebSocketFinalizePlan plan;
            try
            {
                plan = await _adapter.BeginFinalizeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CaptureFault(ex);
                ThrowIfFaulted();
                throw;
            }

            await SendBatchUnderGateAsync(plan.Messages, "finalization", ct);
            if (plan.AlreadyTerminal)
                _terminalCompletion.TrySetResult();
        }
        finally
        {
            _sendGate.Release();
        }

        if (_adapter.Terminal.Required)
            await _terminalCompletion.Task.WaitAsync(ct);

        ThrowIfFaulted();
        if (State is not WebSocketSessionState.Disposing and not WebSocketSessionState.Disposed)
            Volatile.Write(ref _state, (int)WebSocketSessionState.Completed);
    }

    private async Task SendBatchUnderGateAsync(
        IReadOnlyList<WebSocketOutboundMessage> messages,
        string operation,
        CancellationToken ct,
        bool pumpOwned = false
    )
    {
        foreach (var message in messages)
        {
            try
            {
                await _transport.SendAsync(message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Adapters commit state (buffer flush, watermark, sequence number)
                // before the batch reaches the wire, so a cancelled batch leaves an
                // unrecoverable, truncated sequence -- refuse later ops rather than
                // risk mis-ordered or dropped audio.
                if (!pumpOwned)
                    Volatile.Write(ref _sendStreamIndeterminate, 1);

                throw;
            }
            catch (Exception ex)
            {
                CaptureFault(
                    new InvalidOperationException(
                        $"{_adapter.ProviderName} streaming {operation} send failed.",
                        ex
                    )
                );
                ThrowIfFaulted();
            }
        }
    }

    private async Task KeepAliveLoopAsync(
        WebSocketKeepAlivePolicy policy,
        CancellationToken ct
    )
    {
        try
        {
            using var timer = new PeriodicTimer(policy.Interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _sendGate.WaitAsync(ct);
                try
                {
                    if (State != WebSocketSessionState.Active)
                        return;

                    await SendBatchUnderGateAsync(
                        [policy.CreateMessage()],
                        "keepalive",
                        ct,
                        pumpOwned: true
                    );
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Pump-owned teardown.
        }
        catch (Exception ex)
        {
            CaptureFault(ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var receiveBuffer = new byte[_options.ReceiveBufferSize];
        using var messageBuffer = new MemoryStream();
        WebSocketMessageType? messageType = null;

        try
        {
            while (true)
            {
                var chunk = await _transport.ReceiveAsync(receiveBuffer, ct);
                if (chunk.EndOfStream)
                {
                    HandleStreamEnd(isClose: false, chunk);
                    return;
                }

                if (chunk.MessageType == WebSocketMessageType.Close)
                {
                    HandleStreamEnd(isClose: true, chunk);
                    return;
                }

                if (chunk.Count < 0 || chunk.Count > receiveBuffer.Length)
                {
                    throw new InvalidOperationException(
                        $"{_adapter.ProviderName} transport returned an invalid receive count."
                    );
                }

                if (messageType is null)
                    messageType = chunk.MessageType;
                else if (messageType != chunk.MessageType)
                {
                    throw new InvalidOperationException(
                        $"{_adapter.ProviderName} changed message type within a fragmented message."
                    );
                }

                if (messageBuffer.Length + chunk.Count > _options.MaximumMessageSize)
                {
                    throw new InvalidOperationException(
                        $"{_adapter.ProviderName} streaming message exceeded the "
                            + $"{_options.MaximumMessageSize}-byte limit."
                    );
                }

                messageBuffer.Write(receiveBuffer, 0, chunk.Count);
                if (!chunk.EndOfMessage)
                    continue;

                var result = _adapter.HandleMessage(
                    messageType.Value,
                    messageBuffer.GetBuffer().AsMemory(0, checked((int)messageBuffer.Length))
                );
                foreach (var transcript in result.Transcripts)
                    Emit(transcript);

                if (result.Fault is not null)
                {
                    CaptureFault(result.Fault);
                    return;
                }

                if ((result.Signals & WebSocketSessionSignal.Ready) != 0)
                    _readyCompletion.TrySetResult();

                if ((result.Signals & WebSocketSessionSignal.Terminal) != 0)
                {
                    _terminalCompletion.TrySetResult();
                    return;
                }

                messageBuffer.SetLength(0);
                messageType = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Pump-owned teardown.
        }
        catch (WebSocketException ex)
        {
            CaptureFault(
                new InvalidOperationException(
                    $"{_adapter.ProviderName} streaming transport failed.",
                    ex
                )
            );
        }
        catch (Exception ex)
        {
            CaptureFault(ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested && Volatile.Read(ref _sessionFault) is null)
            {
                // Terminal can arrive before readiness, leaving StartAsync stuck on
                // _readyCompletion forever. Fault instead of stranding it.
                if (_adapter.Readiness.Required && !_readyCompletion.Task.IsCompleted)
                {
                    CaptureFault(
                        new InvalidOperationException(
                            $"{_adapter.ProviderName} streaming receive ended before "
                                + $"{_adapter.Readiness.SignalName}."
                        )
                    );
                }
                else if (
                    _adapter.Terminal.Required
                    && !_terminalCompletion.Task.IsCompleted
                )
                {
                    CaptureFault(
                        new InvalidOperationException(
                            $"{_adapter.ProviderName} streaming receive ended before "
                                + $"{_adapter.Terminal.SignalName}."
                        )
                    );
                }
            }
        }
    }

    private void HandleStreamEnd(bool isClose, WebSocketReceiveChunk chunk)
    {
        if (_adapter.Readiness.Required && !_readyCompletion.Task.IsCompleted)
        {
            var close = isClose
                ? FormatClose(chunk.CloseStatus, chunk.CloseDescription)
                : "reached EOF";
            CaptureFault(
                new InvalidOperationException(
                    $"{_adapter.ProviderName} streaming session faulted: socket "
                        + $"{close} before "
                        + $"{_adapter.Readiness.SignalName}."
                )
            );
            return;
        }

        if (_adapter.Terminal.Required && !_terminalCompletion.Task.IsCompleted)
        {
            var close = isClose
                ? FormatClose(chunk.CloseStatus, chunk.CloseDescription)
                : "reached EOF";
            CaptureFault(
                new InvalidOperationException(
                    $"{_adapter.ProviderName} streaming session faulted: socket "
                        + $"{close} before "
                        + $"{_adapter.Terminal.SignalName}."
                )
            );
            return;
        }

        if (
            isClose
            && chunk.CloseStatus is not null
            && chunk.CloseStatus != WebSocketCloseStatus.NormalClosure
        )
        {
            CaptureFault(
                new InvalidOperationException(
                    $"{_adapter.ProviderName} streaming session faulted: socket "
                        + $"{FormatClose(chunk.CloseStatus, chunk.CloseDescription)}."
                )
            );
        }
    }

    private static string FormatClose(
        WebSocketCloseStatus? status,
        string? description
    )
    {
        var statusText = status is { } value
            ? $"closed {(int)value} ({value})"
            : "closed without a close status";
        return string.IsNullOrWhiteSpace(description)
            ? statusText
            : $"{statusText}: {description}";
    }

    private void Emit(StreamingTranscriptEvent transcript)
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(TranscriptReceived))
        {
            try
            {
                subscriber(transcript);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"{_adapter.ProviderName} streaming subscriber failed: {ex.Message}"
                );
            }
        }
    }

    private void CaptureFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _sessionFault, exception, null) is not null)
            return;

        var state = State;
        if (state is not WebSocketSessionState.Disposing and not WebSocketSessionState.Disposed)
            Volatile.Write(ref _state, (int)WebSocketSessionState.Faulted);

        _readyCompletion.TrySetException(exception);
        _terminalCompletion.TrySetException(exception);
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A late transport callback raced the completed resource reaper.
        }
    }

    private void ThrowIfFaulted()
    {
        var exception = Volatile.Read(ref _sessionFault);
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private void ThrowIfSendStreamIndeterminate(string operation)
    {
        if (Volatile.Read(ref _sendStreamIndeterminate) == 0)
            return;

        throw new InvalidOperationException(
            $"{_adapter.ProviderName} cannot {operation}: an earlier send was "
                + "cancelled mid-stream, leaving the outbound protocol state indeterminate."
        );
    }

    private void EnsureState(WebSocketSessionState required, string operation)
    {
        var state = State;
        if (state == required)
            return;
        ObjectDisposedException.ThrowIf(
            state is WebSocketSessionState.Disposing or WebSocketSessionState.Disposed,
            this
        );

        throw new InvalidOperationException(
            $"{_adapter.ProviderName} cannot {operation} while the session is {state}."
        );
    }

    private void BeginDisposal()
    {
        Volatile.Write(ref _state, (int)WebSocketSessionState.Disposing);
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal already reached the resource reaper.
        }

        _readyCompletion.TrySetCanceled(_lifetimeCts.Token);
        _terminalCompletion.TrySetCanceled(_lifetimeCts.Token);
    }

    private async Task DisposeCoreAsync()
    {
        var timeout = _adapter.ClosePolicy.Timeout;
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(2);

        var stopwatch = Stopwatch.StartNew();
        var gateHeld = false;
        Task? closeTask = null;

        try
        {
            gateHeld = await WaitForSendGateAsync(Remaining(timeout, stopwatch));
            if (!gateHeld)
            {
                AbortOnce();
                _ = ReapAfterGateAsync(closeTask);
                return;
            }

            if (
                _transport.State
                is WebSocketState.Open
                    or WebSocketState.CloseReceived
                    or WebSocketState.CloseSent
            )
            {
                var closeCts = new CancellationTokenSource(Remaining(timeout, stopwatch));
                closeTask = _transport
                    .CloseAsync(
                        _adapter.ClosePolicy.Status,
                        _adapter.ClosePolicy.Description,
                        closeCts.Token
                    )
                    .AsTask();
                _ = closeTask.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    closeCts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

                // A close that overruns, faults, or is cancelled leaves the socket
                // state unknown, so fall back to Abort.
                if (
                    !await WaitWithinBudgetAsync(closeTask, Remaining(timeout, stopwatch))
                    || closeTask.IsFaulted
                    || closeTask.IsCanceled
                )
                {
                    AbortOnce();
                }
            }

            var background = BackgroundTasks(closeTask);
            if (!await WaitWithinBudgetAsync(background, Remaining(timeout, stopwatch)))
            {
                AbortOnce();
                _ = ReapHeldGateAsync(background);
                gateHeld = false;
                return;
            }

            await DisposeTransportOnceAsync();
        }
        catch
        {
            AbortOnce();
            var background = BackgroundTasks(closeTask);
            _ = gateHeld
                ? ReapHeldGateAsync(background)
                : ReapAfterGateAsync(closeTask);
            gateHeld = false;
        }
        finally
        {
            if (gateHeld)
                _sendGate.Release();
        }
    }

    private async Task ReapAfterGateAsync(Task? closeTask)
    {
        await _sendGate.WaitAsync();
        try
        {
            await ObserveAsync(BackgroundTasks(closeTask));
            await DisposeTransportOnceAsync();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReapHeldGateAsync(Task background)
    {
        try
        {
            await ObserveAsync(background);
            await DisposeTransportOnceAsync();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Task BackgroundTasks(Task? closeTask)
    {
        var tasks = new List<Task>(3);
        if (closeTask is not null)
            tasks.Add(closeTask);
        if (Volatile.Read(ref _receiveTask) is { } receiveTask)
            tasks.Add(receiveTask);
        if (Volatile.Read(ref _keepAliveTask) is { } keepAliveTask)
            tasks.Add(keepAliveTask);
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            _ = task.Exception;
        }
    }

    private async ValueTask DisposeTransportOnceAsync()
    {
        if (Interlocked.Exchange(ref _transportDisposed, 1) != 0)
            return;

        _ = _readyCompletion.Task.Exception;
        _ = _terminalCompletion.Task.Exception;
        _ = _finalizeTask?.Exception;
        await _transport.DisposeAsync();
        _lifetimeCts.Dispose();
        Volatile.Write(ref _state, (int)WebSocketSessionState.Disposed);
    }

    private void AbortOnce()
    {
        if (Interlocked.Exchange(ref _abortCalled, 1) != 0)
            return;

        try
        {
            _transport.Abort();
        }
        catch
        {
            // Best-effort teardown.
        }
    }

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch stopwatch)
    {
        var remaining = budget - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task<bool> WaitWithinBudgetAsync(
        Task task,
        TimeSpan remaining
    )
    {
        if (task.IsCompleted)
            return true;
        if (remaining <= TimeSpan.Zero)
            return false;

        try
        {
            await task.WaitAsync(remaining);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task<bool> WaitForSendGateAsync(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return false;

        using var cts = new CancellationTokenSource(remaining);
        try
        {
            await _sendGate.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
    }
}
