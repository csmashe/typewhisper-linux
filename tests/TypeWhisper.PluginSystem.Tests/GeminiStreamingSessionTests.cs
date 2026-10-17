using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiStreamingSessionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private const string Final = """{"serverContent":{"inputTranscription":{"text":"Hello world."}}}""";

    // 300 ms of PCM16 at 16 kHz.
    private const int AudioBytes = 300 * 16 * 2;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalizeAsync_WaitsForTheFinalAndTheAcknowledgedEndInEitherOrder(
        bool acknowledgementFirst, bool throughCoordinator)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        if (throughCoordinator)
            await coordinator.StartAsync(CancellationToken.None);
        var finals = new ConcurrentQueue<string>();
        session.TranscriptReceived += e => { if (e.IsFinal) finals.Enqueue(e.Text); };
        await SendAudioAsync(session, transport);

        var finalize = throughCoordinator
            ? coordinator.FinalizeAsync(CancellationToken.None)
            : session.FinalizeAsync(CancellationToken.None);
        Assert.Equal(GeminiStreamingSession.ActivityEndPayload, await NextSentTextAsync(transport));
        transport.EnqueueText(acknowledgementFirst ? ActivityEnd(0.3m) : Final);
        await Task.Delay(250);
        Assert.False(finalize.IsCompleted);
        transport.EnqueueText(acknowledgementFirst ? Final : ActivityEnd(0.3m));
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(["Hello world."], finals);
        Assert.Null(session.Fault);
        if (throughCoordinator)
            Assert.Equal("Hello world.", await (Task<string>)finalize);
    }

    [Fact]
    public async Task FinalizeAsync_AcceptsBothSignalsInOneFrame()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendAudioAsync(session, transport);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(
            """{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"0.3s"},"serverContent":{"inputTranscription":{"text":"Done."}}}""");

        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task Coordinator_WaitsPastItsDefaultDeadlineForTheFinal()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        await coordinator.StartAsync(CancellationToken.None);
        await SendAudioAsync(session, transport);

        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(ActivityEnd(0.3m));
        // Past the 2 s every other provider gets, inside the 15 s Gemini asks for.
        await Task.Delay(2500);
        Assert.False(finalize.IsCompleted);
        transport.EnqueueText(Final);

        Assert.Equal("Hello world.", await finalize.WaitAsync(s_timeout));
    }

    [Fact]
    public async Task FinalizeTimeout_CoversTheProviderFinalizingTheWholeRecording()
    {
        await using var session = await ConnectAsync(new ScriptedWebSocketTransport());

        Assert.Equal(TimeSpan.FromSeconds(15), session.FinalizeTimeout);
    }

    [Fact]
    public async Task FinalizeAsync_EmptyFinalCompletesWithoutText()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        await coordinator.StartAsync(CancellationToken.None);
        await SendAudioAsync(session, transport);

        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":""}}}""");
        transport.EnqueueText(ActivityEnd(0.3m));

        Assert.Equal("", await finalize.WaitAsync(s_timeout));
    }

    [Fact]
    public async Task FinalizeAsync_WithoutAudio_CompletesWithoutSending()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await session.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout);

        Assert.Empty(transport.DrainSent());
    }

    [Fact]
    public async Task InterimsPublishBeforeTheFinal()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Enqueue;
        await SendAudioAsync(session, transport);
        transport.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":" Hello "}}}""");

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(Final);
        transport.EnqueueText(ActivityEnd(0.3m));
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(
            [new StreamingTranscriptEvent("Hello", false), new StreamingTranscriptEvent("Hello world.", true)],
            events);
    }

    public static TheoryData<string, string> InvalidatingEvents => new()
    {
        // The recording is one activity: a final before its end means the server split it.
        { "early final", Final },
        { "early acknowledgement", """{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"0.3s"}}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidatingEvents))]
    public async Task EventBeforeActivityEnd_FaultsForBatchFallback(string _, string json)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        await coordinator.StartAsync(CancellationToken.None);
        await SendAudioAsync(session, transport);

        transport.EnqueueText(json);
        await WaitForFaultAsync(session);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout));
        Assert.Contains("unexpected", error.InnerException!.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, string> InvalidCompletions => new()
    {
        { "second final", Final, Final, "unexpected transcription segment" },
        { "duplicate acknowledgement", ActivityEnd(0.3m), ActivityEnd(0.3m), "unexpected audio boundary" },
        { "short acknowledgement", Final, ActivityEnd(0.2m), "less audio than was sent" },
        { "acknowledgement without offset", Final, """{"voiceActivity":{"type":"ACTIVITY_END"}}""", "less audio than was sent" },
        { "malformed offset", Final, """{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"0.3"}}""", "less audio than was sent" },
        { "final without text", ActivityEnd(0.3m), """{"serverContent":{"inputTranscription":{}}}""", "without text" },
    };

    [Theory]
    [MemberData(nameof(InvalidCompletions))]
    public async Task InvalidCompletion_FaultsForBatchFallback(
        string _, string first, string invalid, string expected)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        await coordinator.StartAsync(CancellationToken.None);
        await SendAudioAsync(session, transport);

        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(first);
        transport.EnqueueText(invalid);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        Assert.Contains(expected, error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcknowledgementWithinRoundingOfTheSentAudio_Completes()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendAudioAsync(session, transport);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(Final);
        transport.EnqueueText(ActivityEnd(0.295m));

        await finalize.WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure)]
    [InlineData(WebSocketCloseStatus.InternalServerError)]
    public async Task CloseBeforeCompletion_FaultsForBatchFallback(WebSocketCloseStatus status)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await using var coordinator = CreateCoordinator(session);
        await coordinator.StartAsync(CancellationToken.None);
        await SendAudioAsync(session, transport);

        var finalize = coordinator.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(Final);
        transport.EnqueueClose(status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        Assert.Contains("before the final transcript and activity end", error.InnerException!.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultDuringFinalize_Throws(bool transportFault)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendAudioAsync(session, transport);

        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        if (transportFault)
            transport.EnqueueFault(new WebSocketException("tail failed"));
        else
            transport.EnqueueText("""{"error":{"message":"tail failed"}}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(s_timeout));
        Assert.Contains(transportFault ? "transport failed" : "tail failed", error.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinalizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeAsync_HonorsCancellationAndCanResume()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendAudioAsync(session, transport);
        using var cts = new CancellationTokenSource();
        var finalize = session.FinalizeAsync(cts.Token);
        await transport.NextSentAsync();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion below; CancelAsync would defer it.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalize);

        transport.EnqueueText(Final);
        transport.EnqueueText(ActivityEnd(0.3m));
        // ReSharper disable once MethodSupportsCancellation -- fixed hang guard; the only in-scope token was cancelled on purpose above.
        await session.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout);
    }

    [Fact]
    public async Task SendAudioAsync_AfterFinalize_Throws()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var session = await ConnectAsync(transport);
        await SendAudioAsync(session, transport);
        _ = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAudioAsync(new byte[2], CancellationToken.None));
    }

    [Theory]
    [InlineData("3.5s", 3.5)]
    [InlineData("0s", 0)]
    [InlineData("12s", 12)]
    [InlineData("0.000000001s", 0.000000001)]
    public void TryParseDurationSeconds_ReadsProtobufDurations(string value, double seconds) =>
        Assert.Equal((decimal)seconds, GeminiWebSocketAdapter.TryParseDurationSeconds(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("3.5")]
    [InlineData("-1s")]
    [InlineData("1,5s")]
    [InlineData("abcs")]
    public void TryParseDurationSeconds_RejectsOtherValues(string? value) =>
        Assert.Null(GeminiWebSocketAdapter.TryParseDurationSeconds(value));

    private static string ActivityEnd(decimal seconds) =>
        $$$"""{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"{{{seconds.ToString(CultureInfo.InvariantCulture)}}}s"}}""";

    private static StreamingTranscriptionCoordinator CreateCoordinator(IStreamingSession session)
    {
        var role = new Mock<ITranscriptionEngineRole>();
        role.Setup(x => x.StartStreamingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        return new StreamingTranscriptionCoordinator(
            role.Object, LanguageSelection.Explicit("en"), [], 1, (_, _) => { }, _ => { });
    }

    private static async Task SendAudioAsync(GeminiStreamingSession session, ScriptedWebSocketTransport transport)
    {
        await session.SendAudioAsync(new byte[AudioBytes], CancellationToken.None).WaitAsync(s_timeout);
        Assert.Equal(GeminiStreamingSession.ActivityStartPayload, await NextSentTextAsync(transport));
        await transport.NextSentAsync();
    }

    private static async Task<string> NextSentTextAsync(ScriptedWebSocketTransport transport) =>
        Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span);

    private static async Task WaitForFaultAsync(GeminiStreamingSession session)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (session.Fault is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotNull(session.Fault);
    }

    private static async Task<GeminiStreamingSession> ConnectAsync(ScriptedWebSocketTransport transport)
    {
        var starting = GeminiStreamingSession.CreateConnectedSessionForTests(transport);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"setupComplete":{}}""");
        return await starting.WaitAsync(s_timeout);
    }
}
