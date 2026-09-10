using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiStreamingSessionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinalizeAsync_OldFinalAndDelayedTail_CollectsTail(bool throughCoordinator, bool delayedInterim)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        var finals = new ConcurrentQueue<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Enqueue(e.Text); };
        if (delayedInterim)
            await SendPrefixFinalAsync(session, transport);
        else
            await OpenTailAsync(session, transport);
        await Task.Delay(200);

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(250);
        Assert.False(finalize.IsCompleted);
        if (delayedInterim)
            transport.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"Tail"}}}""");
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail."}}}""");
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(["Prefix.", "Tail."], finals);
        if (throughCoordinator)
            Assert.Equal("Prefix.\nTail.", await (Task<string>)finalize);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinalizeAsync_FaultDuringCollection_Throws(bool transportFault, bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        await OpenTailAsync(session, transport);
        await Task.Delay(200);
        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(100);
        Assert.False(finalize.IsCompleted);
        EnqueueFault(transport, transportFault);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        Assert.Contains(transportFault ? "transport failed" : "tail failed", error.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinalizeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_FaultAfterSuccessfulFinalize_ThrowsOnRepeat(bool transportFault)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await session.FinalizeAsync(CancellationToken.None);
        EnqueueFault(transport, transportFault);
        // A later send observes the pump fault, providing a barrier before the repeated finalize.
        for (var attempt = 0; ; attempt++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
            if (error.ToString().Contains(transportFault ? "transport failed" : "tail failed"))
                break;
            Assert.True(attempt < 100);
            await Task.Delay(10);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinalizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeAsync_PendingTail_HonorsCancellationAndCanResume()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await OpenTailAsync(session, transport);
        using var cts = new CancellationTokenSource();
        var finalize = session.FinalizeAsync(cts.Token);
        await transport.NextSentAsync();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion below; CancelAsync would defer it.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalize);
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail."}}}""");
        // ReSharper disable once MethodSupportsCancellation -- fixed hang guard; the only in-scope token was cancelled on purpose above.
        await session.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout);
    }

    [Fact]
    public async Task FinalizeAsync_MissingTail_TimesOut()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await OpenTailAsync(session, transport);
        await Assert.ThrowsAsync<TimeoutException>(() => session.FinalizeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure)]
    [InlineData(WebSocketCloseStatus.InternalServerError)]
    public async Task FinalizeAsync_CloseDuringCollection_StopsWaiting(WebSocketCloseStatus status)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await OpenTailAsync(session, transport);
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose(status);
        if (status == WebSocketCloseStatus.NormalClosure)
        {
            var error = await Assert.ThrowsAsync<PluginRequestException>(
                () => finalize.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(PluginRequestFailureKind.ServerError, error.FailureKind);
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => finalize.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Coordinator_NormalCloseWithPendingTail_ResultIsIneligible()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        await coordinator.StartAsync(CancellationToken.None);
        await OpenTailAsync(session, transport);
        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        var providerError = Assert.IsType<PluginRequestException>(error.InnerException);
        Assert.Equal(PluginRequestFailureKind.ServerError, providerError.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_FaultAfterSessionFinalizeDuringGrace_ResultIsIneligible(bool transportFault)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var observed = new ObservedSession(session);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(observed);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        await coordinator.StartAsync(CancellationToken.None);
        await OpenTailAsync(session, transport);
        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail."}}}""");
        await observed.Finalized.Task.WaitAsync(s_timeout);
        Assert.False(finalize.IsCompleted);
        Assert.Null(session.Fault);
        EnqueueFault(transport, transportFault);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        Assert.Same(session.Fault, error.InnerException);
        Assert.Contains(transportFault ? "transport failed" : "tail failed", error.ToString());
    }

    [Fact]
    public async Task FinalizeAsync_NoInterim_HonorsCancellation()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        using var cts = new CancellationTokenSource();
        var finalize = session.FinalizeAsync(cts.Token);
        await transport.NextSentAsync();
        Assert.False(finalize.IsCompleted);
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion below; CancelAsync would defer it.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_AudibleTailWithoutTranscript_FailsForBatchFallback(bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        await SendPrefixFinalAsync(session, transport);
        await Task.Delay(1);
        await session.SendAudioAsync(AudibleAudio(300), CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(50);

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(250);
        Assert.False(finalize.IsCompleted);
        if (throughCoordinator)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => finalize.WaitAsync(s_timeout));
            Assert.Contains("did not arrive in time", error.Message);
        }
    }

    [Fact]
    public async Task FinalizeAsync_AudibleTailAndNormalClose_Throws()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendPrefixFinalAsync(session, transport);
        await Task.Delay(1);
        await session.SendAudioAsync(AudibleAudio(300), CancellationToken.None);
        await transport.NextSentAsync();
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => finalize.WaitAsync(s_timeout));
        Assert.Equal(PluginRequestFailureKind.ServerError, error.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_AudibleTailWithDelayedFirstEvent_CollectsTail(bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        var finals = new ConcurrentQueue<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Enqueue(e.Text); };
        await SendPrefixFinalAsync(session, transport);
        await Task.Delay(1);
        await session.SendAudioAsync(AudibleAudio(300), CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(50);

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        await Task.Delay(900);
        transport.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"Tail"}}}""");
        await Task.Delay(100);
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail."}}}""");
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(["Prefix.", "Tail."], finals);
        if (throughCoordinator)
            Assert.Equal("Prefix.\nTail.", await (Task<string>)finalize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_SilentTail_CompletesWithoutTail(bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        var finals = new ConcurrentQueue<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Enqueue(e.Text); };
        await session.SendAudioAsync(AudibleAudio(300), CancellationToken.None);
        await transport.NextSentAsync();
        await SendPrefixFinalAsync(session, transport);
        await session.SendAudioAsync(SilentAudio(500), CancellationToken.None);
        await transport.NextSentAsync();

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(["Prefix."], finals);
        if (throughCoordinator)
            Assert.Equal("Prefix.", await (Task<string>)finalize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_FinalsWithoutInterims_AreCollectedForTheWholeWindow(bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        await using var coordinator = new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        var finals = new ConcurrentQueue<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Enqueue(e.Text); };
        await session.SendAudioAsync(AudibleAudio(300), CancellationToken.None);
        await transport.NextSentAsync();

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        var delivery = SendFinalsAsync();
        await finalize.WaitAsync(s_timeout);
        var collectedFinals = finals.ToArray();
        await delivery;

        Assert.Equal(["Tail one.", "Tail two."], collectedFinals);
        if (throughCoordinator)
            Assert.Equal("Tail one.\nTail two.", await (Task<string>)finalize);
        return;

        async Task SendFinalsAsync()
        {
            await Task.Delay(50);
            transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail one."}}}""");
            await Task.Delay(350);
            transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Tail two."}}}""");
        }
    }

    [Fact]
    public void IsAudible_DetectsSpeechLevelSamples()
    {
        Assert.False(GeminiStreamingSession.IsAudible([]));
        Assert.False(GeminiStreamingSession.IsAudible([0]));
        Assert.False(GeminiStreamingSession.IsAudible(SilentAudio(100)));
        var quietAudio = new byte[100 * 16 * 2];
        for (var sample = 0; sample < quietAudio.Length / 2; sample++)
            BinaryPrimitives.WriteInt16LittleEndian(quietAudio.AsSpan(sample * 2), 200);
        Assert.False(GeminiStreamingSession.IsAudible(quietAudio));
        var audibleAudio = AudibleAudio(100);
        Assert.True(GeminiStreamingSession.IsAudible(audibleAudio));
        Assert.True(GeminiStreamingSession.IsAudible([.. audibleAudio, 0]));
    }

    private static byte[] AudibleAudio(int milliseconds)
    {
        var audio = new byte[milliseconds * 16 * 2];
        for (var sample = 0; sample < audio.Length / 2; sample++)
            BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(sample * 2), (short)(sample % 40 < 20 ? 8000 : -8000));
        return audio;
    }

    private static byte[] SilentAudio(int milliseconds) => new byte[milliseconds * 16 * 2];

    private static async Task SendPrefixFinalAsync(GeminiStreamingSession session, ScriptedWebSocketTransport transport)
    {
        var prefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranscriptReceived += e => { if (e.IsFinal) prefix.TrySetResult(); };
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Prefix."}}}""");
        await prefix.Task.WaitAsync(s_timeout);
    }

    private sealed class ObservedSession(GeminiStreamingSession inner) : IStreamingSession, IStreamingSessionHealth
    {
        internal TaskCompletionSource Finalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Fault => inner.Fault;
        public event Action<StreamingTranscriptEvent>? TranscriptReceived
        {
            add => inner.TranscriptReceived += value;
            remove => inner.TranscriptReceived -= value;
        }
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => inner.SendAudioAsync(audio, ct);
        public async Task FinalizeAsync(CancellationToken ct)
        {
            await inner.FinalizeAsync(ct);
            Finalized.TrySetResult();
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static void EnqueueFault(ScriptedWebSocketTransport transport, bool transportFault)
    {
        if (transportFault)
            transport.EnqueueFault(new WebSocketException("tail failed"));
        else
            transport.EnqueueText("""{"error":{"message":"tail failed"}}""");
    }

    private static async Task<GeminiStreamingSession> ConnectAsync(ScriptedWebSocketTransport transport)
    {
        var starting = GeminiStreamingSession.CreateConnectedSessionForTests(transport);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"setupComplete":{}}""");
        return await starting.WaitAsync(s_timeout);
    }

    private static async Task OpenTailAsync(GeminiStreamingSession session, ScriptedWebSocketTransport transport)
    {
        var interim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranscriptReceived += e => { if (!e.IsFinal) interim.TrySetResult(); };
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"Prefix."}}}""");
        transport.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"Tail"}}}""");
        await interim.Task.WaitAsync(s_timeout);
    }
}
