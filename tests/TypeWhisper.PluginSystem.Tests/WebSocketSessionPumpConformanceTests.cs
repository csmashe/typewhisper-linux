using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WebSocketSessionPumpConformanceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ErrorAfterPartialMessage_FaultsWithoutDispatchingTruncatedPayload()
    {
        var transport = new ScriptedWebSocketTransport();
        var adapter = new TestAdapter();
        await using var pump = await StartAsync(adapter, transport);

        transport.EnqueueText("""{"type":""", endOfMessage: false);
        transport.EnqueueFault(new WebSocketException("synthetic receive failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pump.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout)
        );
        Assert.Contains("transport failed", exception.Message);
        Assert.Empty(adapter.Messages);
    }

    [Fact]
    public async Task EarlyEofBeforeRequiredTerminal_FaultsFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueEof();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.Contains("reached EOF before terminal", exception.Message);
    }

    [Fact]
    public async Task NormalCloseBeforeRequiredTerminal_IsIncompleteFailure()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.Contains("before terminal", exception.Message);
    }

    [Fact]
    public async Task AbnormalCloseBeforeTerminal_PreservesStatusAndReason()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose(
            WebSocketCloseStatus.InternalServerError,
            "provider restarted"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.Contains("1011", exception.Message);
        Assert.Contains("provider restarted", exception.Message);
    }

    [Fact]
    public async Task ConcurrentSendAndFinalize_AreLinearizedAndTerminalNeverOvertakesAudio()
    {
        var transport = new ScriptedWebSocketTransport();
        transport.BlockNextSend();
        await using var pump = await StartAsync(new TestAdapter(), transport);

        var send = pump.SendAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await transport.WaitForBlockedSendAsync().WaitAsync(s_timeout);
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        Assert.False(finalize.IsCompleted);

        transport.ReleaseBlockedSend();
        await send.WaitAsync(s_timeout);
        var audio = await transport.NextSentAsync();
        var terminal = await transport.NextSentAsync();
        Assert.Equal(WebSocketMessageType.Binary, audio.MessageType);
        Assert.Equal([1, 2, 3], audio.Payload.ToArray());
        Assert.Equal("finish", Encoding.UTF8.GetString(terminal.Payload.Span));

        transport.EnqueueText("terminal");
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task ConcurrentFinalize_SendsTerminalBatchExactlyOnce()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);

        var first = pump.FinalizeAsync(CancellationToken.None);
        var second = pump.FinalizeAsync(CancellationToken.None);
        var terminal = await transport.NextSentAsync();
        Assert.Equal("finish", Encoding.UTF8.GetString(terminal.Payload.Span));
        Assert.Empty(transport.DrainSent());

        transport.EnqueueText("terminal");
        await Task.WhenAll(first, second).WaitAsync(s_timeout);
        Assert.Empty(transport.DrainSent());
    }

    [Fact]
    public async Task CloseTimeout_AbortsOnceAndReturnsWithinTotalBudget()
    {
        var transport = new ScriptedWebSocketTransport
        {
            BlockCloseUntilAbort = true,
        };
        var adapter = new TestAdapter
        {
            ClosePolicyOverride = new WebSocketClosePolicy(
                TimeSpan.FromMilliseconds(150)
            ),
        };
        var pump = await StartAsync(adapter, transport);
        var stopwatch = Stopwatch.StartNew();

        await pump.DisposeAsync().AsTask().WaitAsync(s_timeout);
        await transport.Disposed.WaitAsync(s_timeout);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.AbortCount);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task DisposeBeforeFinalize_DoesNotSendTerminalAndDisposesExactlyOnce()
    {
        var transport = new ScriptedWebSocketTransport();
        var pump = await StartAsync(new TestAdapter(), transport);

        await pump.DisposeAsync().AsTask().WaitAsync(s_timeout);
        await pump.DisposeAsync().AsTask().WaitAsync(s_timeout);

        Assert.Empty(transport.DrainSent());
        Assert.Equal(1, transport.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pump.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout)
        );
    }

    [Fact]
    public async Task ConcurrentDispose_ReturnsOneSharedTask()
    {
        var transport = new ScriptedWebSocketTransport();
        var pump = await StartAsync(new TestAdapter(), transport);

        var first = pump.DisposeAsync().AsTask();
        var second = pump.DisposeAsync().AsTask();

        Assert.Same(first, second);
        await Task.WhenAll(first, second).WaitAsync(s_timeout);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task TerminalFrameRequired_FinalTranscriptIsPublishedBeforeFinalizeCompletes()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        pump.TranscriptReceived += transcript =>
        {
            if (transcript == new StreamingTranscriptEvent("tail", true))
                published.TrySetResult();
        };

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("terminal-with-transcript");
        await finalize.WaitAsync(s_timeout);

        Assert.True(published.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FaultPropagationOrdering_FirstFaultWinsAndReachesSendThenFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);
        transport.EnqueueText("provider-error");

        var finalizeException = await Assert.ThrowsAsync<TestProviderException>(
            () => pump.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout)
        );
        var sendException = await Assert.ThrowsAsync<TestProviderException>(
            () => pump
                .SendAudioAsync(new byte[] { 1 }, CancellationToken.None)
                .WaitAsync(s_timeout)
        );

        Assert.Same(finalizeException, sendException);
        Assert.Equal("first provider fault", sendException.Message);
    }

    [Fact]
    public async Task FragmentedTextAndBinaryMessages_AreAssembledExactly()
    {
        var transport = new ScriptedWebSocketTransport();
        var adapter = new TestAdapter();
        await using var pump = await StartAsync(adapter, transport);

        transport.EnqueueText("hel", endOfMessage: false);
        transport.EnqueueText("lo");
        transport.EnqueueBinary(new byte[] { 1, 2 }, endOfMessage: false);
        transport.EnqueueBinary(new byte[] { 3, 4 });

        await adapter.TwoMessages.Task.WaitAsync(s_timeout);
        Assert.Equal(2, adapter.Messages.Count);
        Assert.Equal(WebSocketMessageType.Text, adapter.Messages[0].Type);
        Assert.Equal("hello"u8.ToArray(), adapter.Messages[0].Payload);
        Assert.Equal(WebSocketMessageType.Binary, adapter.Messages[1].Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, adapter.Messages[1].Payload);
    }

    [Fact]
    public async Task SubscriberException_DoesNotFaultReceiveLoop()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(new TestAdapter(), transport);
        var observed = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += _ => throw new ApplicationException("subscriber");
        pump.TranscriptReceived += observed.Enqueue;

        transport.EnqueueText("transcript");
        transport.EnqueueText("terminal");
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await finalize.WaitAsync(s_timeout);

        Assert.Equal([new StreamingTranscriptEvent("partial", false)], observed);
    }

    [Fact]
    public async Task TerminalBeforeRequiredReadiness_FaultsStartupInsteadOfHanging()
    {
        var transport = new ScriptedWebSocketTransport();
        var adapter = new TestAdapter
        {
            ReadinessOverride = WebSocketReadinessPolicy.Require("readiness"),
        };

        // Provider violates its contract by terminating before signaling readiness;
        // startup must fault rather than wait forever.
        transport.EnqueueText("terminal");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                WebSocketSessionPump
                    .StartConnectedAsync(adapter, transport, CancellationToken.None)
                    .WaitAsync(s_timeout)
        );
        Assert.Contains("before readiness", exception.Message);
    }

    [Fact]
    public async Task ReadinessThenTerminal_CompletesStartupAndFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        var adapter = new TestAdapter
        {
            ReadinessOverride = WebSocketReadinessPolicy.Require("readiness"),
        };

        transport.EnqueueText("ready");
        await using var pump = await WebSocketSessionPump
            .StartConnectedAsync(adapter, transport, CancellationToken.None)
            .WaitAsync(s_timeout);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("terminal");
        await finalize.WaitAsync(s_timeout);
    }

    private static Task<WebSocketSessionPump> StartAsync(
        TestAdapter adapter,
        ScriptedWebSocketTransport transport
    ) =>
        WebSocketSessionPump
            .StartConnectedAsync(adapter, transport, CancellationToken.None)
            .WaitAsync(s_timeout);

    private sealed class TestProviderException(string message) : Exception(message);

    private sealed class TestAdapter : IWebSocketSessionAdapter
    {
        internal List<(WebSocketMessageType Type, byte[] Payload)> Messages { get; } =
            [];
        internal TaskCompletionSource TwoMessages { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WebSocketClosePolicy? ClosePolicyOverride { get; init; }
        internal WebSocketReadinessPolicy? ReadinessOverride { get; init; }

        public string ProviderName => "Test";
        public WebSocketReadinessPolicy Readiness =>
            ReadinessOverride ?? WebSocketReadinessPolicy.Immediate;
        public WebSocketTerminalPolicy Terminal =>
            WebSocketTerminalPolicy.Require("terminal");
        public WebSocketKeepAlivePolicy? KeepAlive => null;
        public WebSocketClosePolicy ClosePolicy =>
            ClosePolicyOverride ?? WebSocketClosePolicy.Default;

        public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
            CancellationToken ct
        ) =>
            ValueTask.FromResult(
                new WebSocketConnectionOptions(new Uri("wss://test.invalid"))
            );

        public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
            CancellationToken ct
        ) =>
            ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

        public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
            ReadOnlyMemory<byte> pcm16Audio,
            CancellationToken ct
        ) =>
            ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
                [new WebSocketOutboundMessage(pcm16Audio.ToArray(), WebSocketMessageType.Binary)]
            );

        public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(
            CancellationToken ct
        ) =>
            ValueTask.FromResult(
                new WebSocketFinalizePlan(
                    [
                        new WebSocketOutboundMessage(
                            "finish"u8.ToArray(),
                            WebSocketMessageType.Text
                        ),
                    ]
                )
            );

        public WebSocketInboundResult HandleMessage(
            WebSocketMessageType type,
            ReadOnlyMemory<byte> completePayload
        )
        {
            var payload = completePayload.ToArray();
            Messages.Add((type, payload));
            if (Messages.Count == 2)
                TwoMessages.TrySetResult();

            var text = type == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(completePayload.Span)
                : null;
            return text switch
            {
                "terminal" => new WebSocketInboundResult(
                    [],
                    WebSocketSessionSignal.Terminal
                ),
                "terminal-with-transcript" => new WebSocketInboundResult(
                    [new StreamingTranscriptEvent("tail", true)],
                    WebSocketSessionSignal.Terminal
                ),
                "provider-error" => new WebSocketInboundResult(
                    [],
                    Fault: new TestProviderException("first provider fault")
                ),
                "ready" => new WebSocketInboundResult(
                    [],
                    WebSocketSessionSignal.Ready
                ),
                "transcript" => new WebSocketInboundResult(
                    [new StreamingTranscriptEvent("partial", false)]
                ),
                _ => WebSocketInboundResult.Empty,
            };
        }
    }
}
