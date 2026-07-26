using System.Net.WebSockets;
using TypeWhisper.PluginSDK;
using Reson8Session = TypeWhisper.Plugin.Reson8.Reson8StreamingSession;
using SmallestAiSession = TypeWhisper.Plugin.SmallestAi.SmallestAiStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StreamingProviderDisposalTests
{
    private static readonly TimeSpan s_signalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_disposalGuard = TimeSpan.FromSeconds(5);

    [Fact]
    public Task SmallestAi_SendHoldingLock_AbortsWithoutConcurrentClose() =>
        AssertSendHoldingLockAsync(SmallestAiSession.CreateConnectedSessionForTests);

    [Fact]
    public Task Reson8_SendHoldingLock_AbortsWithoutConcurrentClose() =>
        AssertSendHoldingLockAsync(Reson8Session.CreateConnectedSessionForTests);

    [Fact]
    public Task SmallestAi_CloseNeverCompletes_AbortsWithinTeardownBudget() =>
        AssertCloseNeverCompletesAsync(SmallestAiSession.CreateConnectedSessionForTests);

    [Fact]
    public Task Reson8_CloseNeverCompletes_AbortsWithinTeardownBudget() =>
        AssertCloseNeverCompletesAsync(Reson8Session.CreateConnectedSessionForTests);

    [Fact]
    public Task SmallestAi_GracefulClose_CompletesWithoutAbort() =>
        AssertGracefulCloseAsync(SmallestAiSession.CreateConnectedSessionForTests);

    [Fact]
    public Task Reson8_GracefulClose_CompletesWithoutAbort() =>
        AssertGracefulCloseAsync(Reson8Session.CreateConnectedSessionForTests);

    [Fact]
    public Task SmallestAi_DeferredCleanup_ObservesLateFaultAndDefersResourceDisposal() =>
        AssertDeferredCleanupAsync(SmallestAiSession.CreateConnectedSessionForTests);

    [Fact]
    public Task Reson8_DeferredCleanup_ObservesLateFaultAndDefersResourceDisposal() =>
        AssertDeferredCleanupAsync(Reson8Session.CreateConnectedSessionForTests);

    private static async Task AssertSendHoldingLockAsync(
        Func<WebSocket, IStreamingSession> createSession)
    {
        var socket = new FakeWebSocket(blockSend: true);
        var session = createSession(socket);
        var sendTask = session.SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await socket.SendStarted.WaitAsync(s_signalTimeout);

        var disposal = session.DisposeAsync().AsTask();
        await disposal.WaitAsync(s_disposalGuard);

        Assert.True(socket.AbortCalled);
        Assert.Equal(0, socket.CloseCallCount);
        Assert.False(socket.CloseCalledWhileSendActive);
        Assert.False(socket.DisposeCalled);

        socket.ReleaseSend();
        await sendTask.WaitAsync(s_signalTimeout);
        await socket.Disposed.WaitAsync(s_signalTimeout);

        Assert.False(socket.DisposedWhileSendActive);
        await session.DisposeAsync();
        Assert.Equal(1, socket.AbortCallCount);
        Assert.Equal(1, socket.DisposeCallCount);
    }

    private static async Task AssertCloseNeverCompletesAsync(
        Func<WebSocket, IStreamingSession> createSession)
    {
        var socket = new FakeWebSocket(blockCloseUntilAbort: true);
        var session = createSession(socket);

        var disposal = session.DisposeAsync().AsTask();
        await socket.CloseStarted.WaitAsync(s_signalTimeout);
        await disposal.WaitAsync(s_disposalGuard);
        await socket.Disposed.WaitAsync(s_signalTimeout);

        Assert.True(socket.AbortCalled);
        Assert.Equal(1, socket.CloseCallCount);
        Assert.False(socket.CloseCalledWhileSendActive);
        Assert.Equal(1, socket.DisposeCallCount);
    }

    private static async Task AssertGracefulCloseAsync(
        Func<WebSocket, IStreamingSession> createSession)
    {
        var socket = new FakeWebSocket();
        var session = createSession(socket);

        var firstDisposal = session.DisposeAsync().AsTask();
        var secondDisposal = session.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(s_disposalGuard);
        await session.DisposeAsync();

        Assert.Equal(1, socket.CloseCallCount);
        Assert.Equal(0, socket.AbortCallCount);
        Assert.Equal(1, socket.DisposeCallCount);
        Assert.False(socket.CloseCalledWhileSendActive);
    }

    private static async Task AssertDeferredCleanupAsync(
        Func<WebSocket, IStreamingSession> createSession)
    {
        var socket = new FakeWebSocket(
            blockSend: true,
            deferReceiveFailure: true
        );
        var session = createSession(socket);
        await socket.ReceiveStarted.WaitAsync(s_signalTimeout);

        var sendTask = session.SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await socket.SendStarted.WaitAsync(s_signalTimeout);

        await session.DisposeAsync().AsTask().WaitAsync(s_disposalGuard);
        Assert.True(socket.AbortCalled);
        Assert.False(socket.DisposeCalled);
        Assert.False(socket.CloseCalledWhileSendActive);

        socket.FailReceive();
        socket.ReleaseSend();
        await sendTask.WaitAsync(s_signalTimeout);
        await socket.Disposed.WaitAsync(s_signalTimeout);

        Assert.False(socket.DisposedWhileSendActive);
        Assert.Equal(1, socket.DisposeCallCount);
    }

    private sealed class FakeWebSocket(
        bool blockSend = false,
        bool blockCloseUntilAbort = false,
        bool deferReceiveFailure = false) : WebSocket
    {
        private readonly TaskCompletionSource _abortSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _receiveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _receiveRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _abortCallCount;
        private int _activeSends;
        private int _closeCallCount;
        private int _disposeCallCount;
        private int _state = (int)WebSocketState.Open;

        public Task CloseStarted => _closeStarted.Task;
        public Task Disposed => _disposed.Task;
        public Task ReceiveStarted => _receiveStarted.Task;
        public Task SendStarted => _sendStarted.Task;
        public int AbortCallCount => Volatile.Read(ref _abortCallCount);
        public bool AbortCalled => AbortCallCount > 0;
        public int CloseCallCount => Volatile.Read(ref _closeCallCount);
        public bool CloseCalledWhileSendActive { get; private set; }
        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);
        public bool DisposeCalled => DisposeCallCount > 0;
        public bool DisposedWhileSendActive { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State =>
            (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;

        public void FailReceive()
        {
            Assert.True(
                ReceiveStarted.IsCompleted,
                "The receive operation did not reach its test signal."
            );
            _receiveRelease.TrySetResult();
        }

        public void ReleaseSend()
        {
            Assert.True(
                SendStarted.IsCompleted,
                "The send operation did not reach its test signal."
            );
            _sendRelease.TrySetResult();
        }

        public override void Abort()
        {
            Interlocked.Increment(ref _abortCallCount);
            Interlocked.Exchange(ref _state, (int)WebSocketState.Aborted);
            _abortSignal.TrySetResult();
        }

        public override async Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _closeCallCount);
            _closeStarted.TrySetResult();
            if (Volatile.Read(ref _activeSends) > 0)
                CloseCalledWhileSendActive = true;

            if (blockCloseUntilAbort)
            {
                // Model a peer that ignores the close cancellation. Abort is
                // what finally unwinds the pending WebSocket operation.
                await _abortSignal.Task;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            if (Volatile.Read(ref _activeSends) > 0)
                DisposedWhileSendActive = true;

            Interlocked.Increment(ref _disposeCallCount);
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            _disposed.TrySetResult();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            _receiveStarted.TrySetResult();
            if (deferReceiveFailure)
            {
                await _receiveRelease.Task;
                throw new ApplicationException("Synthetic late receive failure.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellable receive unexpectedly completed.");
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            SendCoreAsync(cancellationToken);

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            new(SendCoreAsync(cancellationToken));

        private async Task SendCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _activeSends);
            _sendStarted.TrySetResult();
            try
            {
                if (blockSend)
                    await _sendRelease.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _activeSends);
            }
        }

    }
}
