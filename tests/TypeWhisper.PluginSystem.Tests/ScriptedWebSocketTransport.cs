using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

internal sealed class ScriptedWebSocketTransport : IWebSocketTransport
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private readonly Channel<ReceiveStep> _receives =
        Channel.CreateUnbounded<ReceiveStep>();
    private readonly Channel<WebSocketOutboundMessage> _sent =
        Channel.CreateUnbounded<WebSocketOutboundMessage>();
    private readonly TaskCompletionSource _abortSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _sendStarted;
    private TaskCompletionSource? _sendRelease;
    private int _abortCount;
    private int _closeCount;
    private int _disposeCount;
    private int _activeSends;

    internal ScriptedWebSocketTransport(bool connected = true)
    {
        State = connected ? WebSocketState.Open : WebSocketState.None;
    }

    // ReSharper disable UnusedAutoPropertyAccessor.Global -- unread today, but removing them
    // would also strip the ConnectAsync capture and _activeSends bookkeeping that feed them.
    internal WebSocketConnectionOptions? ConnectionOptions { get; private set; }
    internal bool CloseCalledWhileSendActive { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
    internal bool BlockCloseUntilAbort { get; init; }
    internal int AbortCount => Volatile.Read(ref _abortCount);
    internal int CloseCount => Volatile.Read(ref _closeCount);
    internal int DisposeCount => Volatile.Read(ref _disposeCount);
    internal Task Disposed => _disposedSignal.Task;
    public WebSocketState State { get; private set; }

    internal void BlockNextSend()
    {
        _sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _sendRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    internal Task WaitForBlockedSendAsync() =>
        (_sendStarted
            ?? throw new InvalidOperationException("No send was configured to block.")).Task;

    internal void ReleaseBlockedSend() => _sendRelease?.TrySetResult();

    private void EnqueueFragment(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType type,
        bool endOfMessage
    ) =>
        Enqueue(
            new ReceiveStep.Chunk(
                payload.ToArray(),
                type,
                endOfMessage
            )
        );

    internal void EnqueueText(string json, bool endOfMessage = true) =>
        EnqueueFragment(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage);

    internal void EnqueueBinary(ReadOnlyMemory<byte> payload, bool endOfMessage = true) =>
        EnqueueFragment(payload, WebSocketMessageType.Binary, endOfMessage);

    internal void EnqueueClose(
        WebSocketCloseStatus? status = WebSocketCloseStatus.NormalClosure,
        string? description = null
    ) =>
        Enqueue(
            new ReceiveStep.Chunk(
                [],
                WebSocketMessageType.Close,
                true,
                status,
                description
            )
        );

    internal void EnqueueEof() => Enqueue(new ReceiveStep.Eof());

    internal void EnqueueFault(Exception exception) =>
        Enqueue(new ReceiveStep.Fault(exception));

    // Bounded so a pump that never sends fails the test instead of hanging it.
    internal async Task<WebSocketOutboundMessage> NextSentAsync() =>
        await _sent.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);

    internal IReadOnlyList<WebSocketOutboundMessage> DrainSent()
    {
        var messages = new List<WebSocketOutboundMessage>();
        while (_sent.Reader.TryRead(out var message))
            messages.Add(message);
        return messages;
    }

    private void Enqueue(ReceiveStep step)
    {
        if (!_receives.Writer.TryWrite(step))
            throw new InvalidOperationException("The receive script is closed.");
    }

    public ValueTask ConnectAsync(
        WebSocketConnectionOptions options,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        ConnectionOptions = options;
        State = WebSocketState.Open;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendAsync(
        WebSocketOutboundMessage message,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _activeSends);
        try
        {
            // Copy the payload: the pump may reuse the caller's buffer after this returns.
            if (!_sent.Writer.TryWrite(message with { Payload = message.Payload.ToArray() }))
            {
                throw new InvalidOperationException("The send script is closed.");
            }

            var sendStarted = _sendStarted;
            var sendRelease = _sendRelease;
            if (sendStarted is not null && sendRelease is not null)
            {
                sendStarted.TrySetResult();
                await sendRelease.Task;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeSends);
        }
    }

    public async ValueTask<WebSocketReceiveChunk> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken ct
    )
    {
        var step = await _receives.Reader.ReadAsync(ct);
        switch (step)
        {
            case ReceiveStep.Fault fault:
                ExceptionDispatchInfo.Capture(fault.Exception).Throw();
                break;
            case ReceiveStep.Eof:
                return new WebSocketReceiveChunk(
                    0,
                    WebSocketMessageType.Binary,
                    true,
                    EndOfStream: true
                );
            case ReceiveStep.Chunk chunk:
                if (chunk.Payload.Length > buffer.Length)
                    throw new InvalidOperationException("Scripted chunk exceeds receive buffer.");
                chunk.Payload.CopyTo(buffer);
                if (chunk.MessageType == WebSocketMessageType.Close)
                    State = WebSocketState.CloseReceived;
                return new WebSocketReceiveChunk(
                    chunk.Payload.Length,
                    chunk.MessageType,
                    chunk.EndOfMessage,
                    chunk.CloseStatus,
                    chunk.CloseDescription
                );
        }

        throw new UnreachableException();
    }

    public async ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref _closeCount);
        if (Volatile.Read(ref _activeSends) != 0)
            CloseCalledWhileSendActive = true;

        if (BlockCloseUntilAbort)
        {
            await _abortSignal.Task;
            return;
        }

        ct.ThrowIfCancellationRequested();
        State = WebSocketState.Closed;
    }

    public void Abort()
    {
        Interlocked.Increment(ref _abortCount);
        State = WebSocketState.Aborted;
        _abortSignal.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        State = WebSocketState.Closed;
        _receives.Writer.TryComplete();
        _sent.Writer.TryComplete();
        _disposedSignal.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private abstract record ReceiveStep
    {
        internal sealed record Chunk(
            byte[] Payload,
            WebSocketMessageType MessageType,
            bool EndOfMessage,
            WebSocketCloseStatus? CloseStatus = null,
            string? CloseDescription = null
        ) : ReceiveStep;

        internal sealed record Eof : ReceiveStep;
        internal sealed record Fault(Exception Exception) : ReceiveStep;
    }
}
