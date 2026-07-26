using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;
using AssemblyAiSession = TypeWhisper.Plugin.AssemblyAi.AssemblyAiStreamingSession;
using DeepgramSession = TypeWhisper.Plugin.Deepgram.DeepgramStreamingSession;
using ElevenLabsSession = TypeWhisper.Plugin.ElevenLabs.ElevenLabsStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StreamingProviderFailurePropagationTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AssemblyAi_OneFinalThenTransportFault_SendAndFinalizeRethrow()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);
        var finalReceived = FinalReceived(session);

        socket.EnqueueText(
            """{"type":"Turn","transcript":"A complete prefix.","end_of_turn":true,"turn_is_formatted":true}"""
        );
        await finalReceived.Task.WaitAsync(s_testTimeout);
        socket.EnqueueFault(new WebSocketException("AssemblyAI transport failed."));
        await socket.LastReceiveConsumed.WaitAsync(s_testTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAudioAsync(new byte[1600], CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.FinalizeAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task AssemblyAi_TerminationPath_UsesV3TerminateAndCommitsOnlyFormattedTurn()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Enqueue;

        var finalize = session.FinalizeAsync(CancellationToken.None);
        var sent = await socket.NextSentAsync();
        Assert.Equal(WebSocketMessageType.Text, sent.MessageType);
        Assert.Equal("""{"type":"Terminate"}""", sent.Text);
        Assert.False(finalize.IsCompleted);

        socket.EnqueueText(
            """{"type":"Turn","transcript":"unformatted ending","end_of_turn":true,"turn_is_formatted":false}"""
        );
        socket.EnqueueText(
            """{"type":"Turn","transcript":"Formatted ending.","end_of_turn":true,"turn_is_formatted":true}"""
        );
        socket.EnqueueText(
            """{"type":"Termination","audio_duration_seconds":1.0,"session_duration_seconds":1.1}"""
        );

        await finalize.WaitAsync(s_testTimeout);
        Assert.Equal(
            [new StreamingTranscriptEvent("Formatted ending.", true)],
            events.Where(evt => evt.IsFinal)
        );
    }

    [Fact]
    public async Task AssemblyAi_AbnormalCloseBeforeTermination_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueClose(
            WebSocketCloseStatus.InternalServerError,
            "provider restarted"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("before Termination", exception.Message);
        Assert.Contains("provider restarted", exception.Message);
    }

    [Fact]
    public async Task AssemblyAi_ProviderError_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText(
            """{"type":"Error","error_code":3007,"error":"Audio transmission rate exceeded."}"""
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("Audio transmission rate exceeded", exception.Message);
    }

    [Fact]
    public async Task AssemblyAi_MalformedJson_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText("""{"type":"Turn","transcript":""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("malformed JSON", exception.Message);
    }

    [Fact]
    public async Task AssemblyAi_FinalizeWait_HonorsCallerCancellation()
    {
        var socket = new FakeWebSocket();
        await using var session = new AssemblyAiSession(socket);
        using var cts = new CancellationTokenSource();

        var finalize = session.FinalizeAsync(cts.Token);
        await socket.NextSentAsync();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; using cts.Token would abort this wait on the cancellation the test triggers next.
            () => finalize.WaitAsync(s_testTimeout)
        );
    }

    [Fact]
    public async Task AssemblyAi_DisposalCancellation_IsClean()
    {
        var socket = new FakeWebSocket();
        var session = new AssemblyAiSession(socket);

        await session.DisposeAsync().AsTask().WaitAsync(s_testTimeout);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task Deepgram_OneFinalThenTransportFault_SendAndFinalizeRethrow()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);
        var finalReceived = FinalReceived(session);

        socket.EnqueueText(DeepgramResult("A complete prefix.", isFinal: true));
        await finalReceived.Task.WaitAsync(s_testTimeout);
        socket.EnqueueFault(new WebSocketException("Deepgram transport failed."));
        await socket.LastReceiveConsumed.WaitAsync(s_testTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.FinalizeAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task Deepgram_MetadataPath_AwaitsFinalResults()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);
        var finalReceived = FinalReceived(session);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        var sent = await socket.NextSentAsync();
        Assert.Equal("""{"type":"CloseStream"}""", sent.Text);
        Assert.False(finalize.IsCompleted);

        socket.EnqueueText(DeepgramResult("Tail result.", isFinal: true));
        socket.EnqueueText(
            """{"type":"Metadata","request_id":"request-id","duration":1.0}"""
        );

        await finalize.WaitAsync(s_testTimeout);
        Assert.Equal("Tail result.", (await finalReceived.Task.WaitAsync(s_testTimeout)).Text);
    }

    [Fact]
    public async Task Deepgram_AbnormalCloseBeforeMetadata_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueClose(
            WebSocketCloseStatus.EndpointUnavailable,
            "upstream unavailable"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("before Metadata", exception.Message);
        Assert.Contains("upstream unavailable", exception.Message);
    }

    [Fact]
    public async Task Deepgram_ProviderError_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText(
            """{"type":"Error","description":"Project has insufficient credits."}"""
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("insufficient credits", exception.Message);
    }

    [Fact]
    public async Task Deepgram_MalformedResult_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText("""{"type":"Results","channel":{"alternatives":[]}}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("malformed Results", exception.Message);
    }

    [Fact]
    public async Task Deepgram_FinalizeWait_HonorsCallerCancellation()
    {
        var socket = new FakeWebSocket();
        await using var session = new DeepgramSession(socket);
        using var cts = new CancellationTokenSource();

        var finalize = session.FinalizeAsync(cts.Token);
        await socket.NextSentAsync();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; using cts.Token would abort this wait on the cancellation the test triggers next.
            () => finalize.WaitAsync(s_testTimeout)
        );
    }

    [Fact]
    public async Task Deepgram_DisposalCancellation_IsClean()
    {
        var socket = new FakeWebSocket();
        var session = new DeepgramSession(socket);

        await session.DisposeAsync().AsTask().WaitAsync(s_testTimeout);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task ElevenLabs_OneFinalThenTransportFault_SendAndFinalizeRethrow()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);
        var finalReceived = FinalReceived(session);

        socket.EnqueueText(
            """{"message_type":"committed_transcript","text":"A complete prefix."}"""
        );
        await finalReceived.Task.WaitAsync(s_testTimeout);
        socket.EnqueueFault(new WebSocketException("ElevenLabs transport failed."));
        await socket.LastReceiveConsumed.WaitAsync(s_testTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAudioAsync(new byte[3200], CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.FinalizeAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task ElevenLabs_CommittedResultPath_AwaitsFinalCommitResponse()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);
        var finalReceived = FinalReceived(session);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        var sent = await socket.NextSentAsync();
        using (var payload = JsonDocument.Parse(sent.Text))
        {
            Assert.True(payload.RootElement.GetProperty("commit").GetBoolean());
            Assert.Equal("", payload.RootElement.GetProperty("audio_base_64").GetString());
        }

        Assert.False(finalize.IsCompleted);
        socket.EnqueueText(
            """{"message_type":"committed_transcript","text":"Final tail."}"""
        );

        await finalize.WaitAsync(s_testTimeout);
        Assert.Equal("Final tail.", (await finalReceived.Task.WaitAsync(s_testTimeout)).Text);
    }

    [Fact]
    public async Task ElevenLabs_EmptyFinalCommit_CompletesWithoutTranscriptText()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Enqueue;

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        Assert.False(finalize.IsCompleted);
        socket.EnqueueText("""{"message_type":"committed_transcript","text":""}""");

        await finalize.WaitAsync(s_testTimeout);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ElevenLabs_VadCommitBeforeFinalize_DoesNotSatisfyFinalCommitWait()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);
        var vadFinalReceived = FinalReceived(session);

        socket.EnqueueText(
            """{"message_type":"committed_transcript","text":"Earlier VAD segment."}"""
        );
        await vadFinalReceived.Task.WaitAsync(s_testTimeout);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        Assert.False(finalize.IsCompleted);

        socket.EnqueueText("""{"message_type":"committed_transcript","text":""}""");
        await finalize.WaitAsync(s_testTimeout);
    }

    [Fact]
    public async Task ElevenLabs_AbnormalCloseBeforeCommittedResult_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueClose(
            WebSocketCloseStatus.InternalServerError,
            "transcriber stopped"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("before the final committed transcript", exception.Message);
        Assert.Contains("transcriber stopped", exception.Message);
    }

    [Theory]
    [InlineData("auth_error")]
    [InlineData("quota_exceeded")]
    [InlineData("transcriber_error")]
    [InlineData("input_error")]
    [InlineData("error")]
    [InlineData("commit_throttled")]
    [InlineData("unaccepted_terms")]
    [InlineData("rate_limited")]
    [InlineData("queue_overflow")]
    [InlineData("resource_exhausted")]
    [InlineData("session_time_limit_exceeded")]
    [InlineData("chunk_size_exceeded")]
    [InlineData("insufficient_audio_activity")]
    [InlineData("scribe_auth_error")]
    [InlineData("scribe_error")]
    public async Task ElevenLabs_DocumentedProviderErrorType_FaultsFinalize(
        string messageType
    )
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText(
            JsonSerializer.Serialize(
                new
                {
                    message_type = messageType,
                    error = "Provider rejected the stream.",
                }
            )
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("Provider rejected the stream", exception.Message);
    }

    [Fact]
    public async Task ElevenLabs_MalformedJson_FaultsFinalize()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await socket.NextSentAsync();
        socket.EnqueueText("""{"message_type":"partial_transcript","text":""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_testTimeout)
        );
        Assert.Contains("malformed JSON", exception.Message);
    }

    [Fact]
    public async Task ElevenLabs_FinalizeWait_HonorsCallerCancellation()
    {
        var socket = new FakeWebSocket();
        await using var session = new ElevenLabsSession(socket);
        using var cts = new CancellationTokenSource();

        var finalize = session.FinalizeAsync(cts.Token);
        await socket.NextSentAsync();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; using cts.Token would abort this wait on the cancellation the test triggers next.
            () => finalize.WaitAsync(s_testTimeout)
        );
    }

    [Fact]
    public async Task ElevenLabs_DisposalCancellation_IsClean()
    {
        var socket = new FakeWebSocket();
        var session = new ElevenLabsSession(socket);

        await session.DisposeAsync().AsTask().WaitAsync(s_testTimeout);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private static TaskCompletionSource<StreamingTranscriptEvent> FinalReceived(
        IStreamingSession session
    )
    {
        var completion = new TaskCompletionSource<StreamingTranscriptEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        session.TranscriptReceived += transcriptEvent =>
        {
            if (transcriptEvent.IsFinal)
                completion.TrySetResult(transcriptEvent);
        };
        return completion;
    }

    private static string DeepgramResult(string text, bool isFinal) =>
        JsonSerializer.Serialize(
            new
            {
                type = "Results",
                is_final = isFinal,
                channel = new
                {
                    alternatives = new[] { new { transcript = text } },
                },
            }
        );

    private sealed record SentFrame(byte[] Payload, WebSocketMessageType MessageType)
    {
        public string Text => Encoding.UTF8.GetString(Payload);
    }

    private abstract record ReceiveItem
    {
        public sealed record Frame(
            byte[] Payload,
            WebSocketMessageType MessageType,
            WebSocketCloseStatus? CloseStatus = null,
            string? CloseDescription = null
        ) : ReceiveItem;

        public sealed record Fault(Exception Exception) : ReceiveItem;
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Channel<ReceiveItem> _receives =
            Channel.CreateUnbounded<ReceiveItem>();
        private readonly Channel<SentFrame> _sends =
            Channel.CreateUnbounded<SentFrame>();
        private TaskCompletionSource _lastReceiveConsumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;

        public Task LastReceiveConsumed => _lastReceiveConsumed.Task;
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueText(string json) =>
            Enqueue(
                new ReceiveItem.Frame(
                    Encoding.UTF8.GetBytes(json),
                    WebSocketMessageType.Text
                )
            );

        public void EnqueueClose(
            WebSocketCloseStatus closeStatus,
            string? closeDescription
        ) =>
            Enqueue(
                new ReceiveItem.Frame(
                    [],
                    WebSocketMessageType.Close,
                    closeStatus,
                    closeDescription
                )
            );

        public void EnqueueFault(Exception exception) =>
            Enqueue(new ReceiveItem.Fault(exception));

        public async Task<SentFrame> NextSentAsync() =>
            await _sends.Reader.ReadAsync().AsTask().WaitAsync(s_testTimeout);

        private void Enqueue(ReceiveItem item)
        {
            _lastReceiveConsumed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Assert.True(_receives.Writer.TryWrite(item));
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _receives.Writer.TryComplete();
            _sends.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            var item = await _receives.Reader.ReadAsync(cancellationToken);
            _lastReceiveConsumed.TrySetResult();

            if (item is ReceiveItem.Fault fault)
            {
                _state = WebSocketState.Aborted;
                ExceptionDispatchInfo.Capture(fault.Exception).Throw();
            }

            var frame = Assert.IsType<ReceiveItem.Frame>(item);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                _closeStatus = frame.CloseStatus;
                _closeDescription = frame.CloseDescription;
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true,
                    frame.CloseStatus,
                    frame.CloseDescription
                );
            }

            Assert.True(
                frame.Payload.Length <= buffer.Count,
                "Fake WebSocket frame exceeds the session receive buffer."
            );
            frame.Payload.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(
                frame.Payload.Length,
                frame.MessageType,
                true
            );
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != WebSocketState.Open)
                throw new WebSocketException("The fake WebSocket is not open.");

            Assert.True(endOfMessage);
            Assert.True(
                _sends.Writer.TryWrite(new SentFrame(buffer.AsSpan().ToArray(), messageType))
            );
            return Task.CompletedTask;
        }
    }
}
