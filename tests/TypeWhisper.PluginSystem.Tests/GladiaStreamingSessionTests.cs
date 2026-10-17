using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GladiaStreamingSessionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Final_CarriesTheUtteranceLanguage()
    {
        var result = Handle(CreateAdapter(), Transcript("1", "Hallo Welt.", true, "de"));

        var transcript = Assert.Single(result.Transcripts);
        Assert.True(transcript.IsFinal);
        Assert.Equal("Hallo Welt.", transcript.Text);
        Assert.Equal("de", transcript.DetectedLanguage);
    }

    [Fact]
    public void RepeatedFinal_IsInsertedOnce()
    {
        var adapter = CreateAdapter();
        Assert.Single(Handle(adapter, Transcript("1", "Hello.", true)).Transcripts);

        var repeated = Handle(adapter, Transcript("1", "Hello.", true));

        Assert.Empty(repeated.Transcripts);
        Assert.Null(repeated.Fault);
    }

    [Fact]
    public void ConflictingFinalForOneUtterance_Faults()
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("1", "Hello.", true));

        AssertIncomplete(Handle(adapter, Transcript("1", "Goodbye.", true)));
    }

    [Fact]
    public void PartialAfterItsFinal_IsIgnored()
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("1", "Hello.", true));

        var late = Handle(adapter, Transcript("1", "Hel", false));

        Assert.Empty(late.Transcripts);
        Assert.Null(late.Fault);
    }

    [Fact]
    public async Task EndSession_AfterEveryUtteranceIsConfirmed_IsTerminal()
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("1", "Hel", false));
        Handle(adapter, Transcript("1", "Hello.", true));
        Handle(adapter, Transcript("2", " ", false));
        await adapter.BeginFinalizeAsync(CancellationToken.None);

        var result = Handle(adapter, """{"type":"end_session","data":{}}""");

        Assert.Null(result.Fault);
        Assert.Equal(WebSocketSessionSignal.Terminal, result.Signals);
    }

    [Fact]
    public async Task EndSession_WithAnUnconfirmedUtterance_Faults()
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("1", "Hello.", true));
        Handle(adapter, Transcript("2", "and then", false));
        await adapter.BeginFinalizeAsync(CancellationToken.None);

        AssertIncomplete(Handle(adapter, """{"type":"end_session","data":{}}"""));
    }

    [Fact]
    public async Task EndSession_AfterAPartialALaterUtteranceSuperseded_IsTerminal()
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("1", "um", false, start: 0.5));
        Handle(adapter, Transcript("2", "Hello.", true, start: 1.2));
        await adapter.BeginFinalizeAsync(CancellationToken.None);

        var result = Handle(adapter, """{"type":"end_session","data":{}}""");

        Assert.Null(result.Fault);
        Assert.Equal(WebSocketSessionSignal.Terminal, result.Signals);
    }

    [Theory]
    [InlineData(4.0, 0.0)]
    [InlineData(null, 0.0)]
    [InlineData(4.0, null)]
    public async Task EndSession_AfterAnEarlierFinal_StillRequiresTheLaterPartial(
        double? partialStart, double? finalStart)
    {
        var adapter = CreateAdapter();
        Handle(adapter, Transcript("2", "and then", false, start: partialStart));
        Handle(adapter, Transcript("1", "Hello.", true, start: finalStart));
        await adapter.BeginFinalizeAsync(CancellationToken.None);

        AssertIncomplete(Handle(adapter, """{"type":"end_session","data":{}}"""));
    }

    [Fact]
    public void AbandonedPartials_DoNotAccumulateTowardTheLimit()
    {
        var adapter = CreateAdapter();
        for (var i = 0; i < 200; i++)
        {
            Assert.Null(Handle(adapter, Transcript($"p{i}", "um", false, start: 2 * i)).Fault);
            Assert.Null(Handle(adapter, Transcript($"f{i}", "Hello.", true, start: 2 * i + 1)).Fault);
        }
    }

    [Fact]
    public void EndSession_BeforeRecordingStopped_Faults()
    {
        AssertIncomplete(Handle(CreateAdapter(), """{"type":"end_session","data":{}}"""));
    }

    [Theory]
    [InlineData("garbage {")]
    [InlineData("[]")]
    [InlineData("""{"type":"transcript","data":{"is_final":true,"utterance":{"text":"a"}}}""")]
    [InlineData("""{"type":"transcript","data":{"id":"1","is_final":true}}""")]
    public void MalformedMessage_Faults(string json)
    {
        AssertIncomplete(Handle(CreateAdapter(), json));
    }

    [Fact]
    public void TooManyUnconfirmedUtterances_Faults()
    {
        var adapter = CreateAdapter();
        for (var i = 0; i < 100; i++)
            Assert.Null(Handle(adapter, Transcript($"{i}", "a", false)).Fault);

        AssertIncomplete(Handle(adapter, Transcript("100", "a", false)));
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(402, PluginRequestFailureKind.Permission)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(408, PluginRequestFailureKind.Timeout)]
    [InlineData(409, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public void RejectedAcknowledgment_IsClassifiedWithoutEchoingThePayload(
        int code, PluginRequestFailureKind kind)
    {
        var result = Handle(CreateAdapter(), $$$"""
            {"type":"stop_recording","acknowledged":false,
             "error":{"status_code":{{{code}}},"exception":"ConflictError","message":"rejected"},
             "data":{"note":"echoed-secret"}}
            """);

        Assert.Empty(result.Transcripts);
        var exception = Assert.IsType<PluginRequestException>(result.Fault);
        Assert.Equal(kind, exception.FailureKind);
        Assert.Equal(code, exception.HttpStatusCode);
        Assert.Equal("Gladia streaming error: ConflictError: rejected", exception.Message);
    }

    [Theory]
    [InlineData("""{"type":"audio_chunk","acknowledged":false,"error":null,"data":{}}""", "Gladia streaming error: request rejected")]
    [InlineData("""{"type":"audio_chunk","acknowledged":true,"error":{"status_code":"429"},"data":{}}""", "Gladia streaming error: HTTP 429")]
    [InlineData("""{"type":"error","message":"rejected","note":"echoed-secret"}""", "Gladia streaming error: rejected")]
    [InlineData("""{"type":"audio_chunk","acknowledged":false,"error":"rejected","data":{}}""", "Gladia streaming error: rejected")]
    public void ErrorWithoutAllFields_StillFaults(string json, string expected)
    {
        var exception = Assert.IsType<PluginRequestException>(Handle(CreateAdapter(), json).Fault);

        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public async Task Finalize_CompletesOnEndSessionWithEveryFinalOnce()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += events.Enqueue;
        transport.EnqueueText(Transcript("1", "Hello", false));

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        Assert.Equal("""{"type":"stop_recording"}""", Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span));
        transport.EnqueueText("""{"type":"stop_recording","acknowledged":true,"error":null,"data":{}}""");
        transport.EnqueueText(Transcript("1", "Hello world.", true, "en"));
        transport.EnqueueText(Transcript("1", "Hello world.", true, "en"));
        transport.EnqueueText("""{"type":"end_recording","data":{}}""");
        transport.EnqueueText("""{"type":"end_session","data":{}}""");
        transport.EnqueueClose();

        await finalize.WaitAsync(s_timeout);
        var final = Assert.Single(events, transcript => transcript.IsFinal);
        Assert.Equal("Hello world.", final.Text);
        Assert.Equal("en", final.DetectedLanguage);
    }

    private static string Transcript(
        string id, string text, bool isFinal, string? language = null, double? start = null)
    {
        var languageField = language is null ? "" : $",\"language\":\"{language}\"";
        var startField = start is null
            ? ""
            : ",\"start\":" + start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var final = isFinal ? "true" : "false";
        return "{\"type\":\"transcript\",\"data\":{\"id\":\"" + id + "\",\"is_final\":" + final
            + ",\"utterance\":{\"text\":\"" + text + "\"" + startField + languageField + "}}}";
    }

    private static void AssertIncomplete(WebSocketInboundResult result)
    {
        Assert.Empty(result.Transcripts);
        Assert.Equal(WebSocketSessionSignal.None, result.Signals);
        var exception = Assert.IsType<PluginRequestException>(result.Fault);
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, exception.FailureKind);
    }

    private static Task<WebSocketSessionPump> StartAsync(ScriptedWebSocketTransport transport) =>
        WebSocketSessionPump
            .StartConnectedAsync(CreateAdapter(), transport, CancellationToken.None)
            .WaitAsync(s_timeout);

    private static GladiaWebSocketAdapter CreateAdapter() => new(new HttpClient(), "key", null, []);

    private static WebSocketInboundResult Handle(GladiaWebSocketAdapter adapter, string json) =>
        adapter.HandleMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));
}
