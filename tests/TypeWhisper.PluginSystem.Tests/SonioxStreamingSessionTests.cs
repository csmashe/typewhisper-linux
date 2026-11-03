using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SonioxStreamingSessionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ParseMessage_PreservesTokenLanguages()
    {
        var message = SonioxStreamingSession.ParseMessage(
            """{"tokens":[{"text":"Hallo","is_final":true,"language":"de"},{"text":" Welt","is_final":false}]}""");

        Assert.Equal("de", message.Tokens[0].Language);
        Assert.Null(message.Tokens[1].Language);
    }

    [Theory]
    [InlineData("\"de\"", "\"de\"", "de")]
    [InlineData("\"de\"", "\"DE\"", "de")]
    [InlineData("\"de\"", "\"en\"", null)]
    [InlineData("null", "null", null)]
    [InlineData("\" \"", "null", null)]
    public async Task Finished_UsesLanguagesFromFinalTokensAcrossMessages(
        string firstLanguage, string secondLanguage, string? expected)
    {
        var adapter = CreateAdapter();
        Handle(adapter, $$"""
            {"tokens":[{"text":"Hallo","is_final":true,"language":{{firstLanguage}}},
                       {"text":" provisional","is_final":false,"language":"fr"}]}
            """);
        await adapter.BeginFinalizeAsync(CancellationToken.None);
        var result = Handle(adapter, $$"""
            {"tokens":[{"text":" Welt","is_final":true,"language":{{secondLanguage}}}],"finished":true}
            """);

        var transcript = Assert.Single(result.Transcripts);
        Assert.True(transcript.IsFinal);
        Assert.Equal("Hallo Welt", transcript.Text);
        Assert.Equal(expected, transcript.DetectedLanguage);
    }

    [Fact]
    public async Task Finished_WithoutTokenLanguages_ReportsNoLanguage()
    {
        var adapter = CreateAdapter();
        await adapter.BeginFinalizeAsync(CancellationToken.None);
        var result = Handle(adapter,
            """{"tokens":[{"text":"Hallo","is_final":true}],"finished":true}""");

        Assert.Null(Assert.Single(result.Transcripts).DetectedLanguage);
    }

    [Theory]
    [InlineData("de", "de", "de")]
    [InlineData("de", "DE", "de")]
    [InlineData("de", "en", null)]
    [InlineData("de", " ", "de")]
    [InlineData("", " ", null)]
    public void Preview_UsesOnlyLanguagesInCurrentMessage(string first, string second, string? expected)
    {
        var adapter = CreateAdapter();
        Handle(adapter, """{"tokens":[{"text":"Vorher. ","is_final":true,"language":"fr"}]}""");
        var result = Handle(adapter, $$"""
            {"tokens":[{"text":"Hallo","is_final":true,"language":"{{first}}"},
                       {"text":" Welt","is_final":false,"language":"{{second}}"}]}
            """);

        var transcript = Assert.Single(result.Transcripts);
        Assert.False(transcript.IsFinal);
        Assert.Equal("Vorher. Hallo Welt", transcript.Text);
        Assert.Equal(expected, transcript.DetectedLanguage);
    }

    [Fact]
    public async Task Connect_SendsKeyInAuthorizationHeaderAndNotInStartMessage()
    {
        var transport = new ScriptedWebSocketTransport(connected: false);
        await using var pump = await WebSocketSessionPump.ConnectAsync(
            new SonioxWebSocketAdapter("fixture-key", new Uri("wss://stt-rt.eu.soniox.com/transcribe-websocket"), ["de"]),
            CancellationToken.None,
            transportFactory: new ScriptedWebSocketTransportFactory(transport)
        ).WaitAsync(s_timeout);

        var options = Assert.IsType<WebSocketConnectionOptions>(transport.ConnectionOptions);
        Assert.Equal("Bearer fixture-key", options.Headers!["Authorization"]);
        var start = Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span);
        Assert.DoesNotContain("fixture-key", start);
        using var config = JsonDocument.Parse(start);
        Assert.Equal("stt-rt-v5", config.RootElement.GetProperty("model").GetString());
        Assert.True(config.RootElement.GetProperty("enable_language_identification").GetBoolean());
    }

    [Fact]
    public async Task FinishedAfterEndOfAudio_PublishesConfirmedTranscriptOnce()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += events.Enqueue;

        transport.EnqueueText("""{"tokens":[{"text":"Hello","is_final":true},{"text":" wor","is_final":false}]}""");
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var endOfAudio = await transport.NextSentAsync();
        Assert.Equal(WebSocketMessageType.Text, endOfAudio.MessageType);
        Assert.True(endOfAudio.Payload.IsEmpty);
        transport.EnqueueText("""{"tokens":[{"text":" world","is_final":true},{"text":"<fin>","is_final":true}]}""");
        transport.EnqueueText("""{"tokens":[],"final_audio_proc_ms":1560,"total_audio_proc_ms":1680,"finished":true}""");
        await finalize.WaitAsync(s_timeout);

        var final = Assert.Single(events, transcript => transcript.IsFinal);
        Assert.Equal("Hello world", final.Text);
        Assert.Null(pump.Fault);
    }

    [Fact]
    public async Task FinishedWithoutSpeech_CompletesWithoutTranscript()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += events.Enqueue;

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"tokens":[],"finished":true}""");
        await finalize.WaitAsync(s_timeout);

        Assert.Empty(events);
        Assert.Null(pump.Fault);
    }

    [Theory]
    // Finished before the host sent end of audio: everything after it would be lost.
    [InlineData(false, """{"tokens":[{"text":"Hello","is_final":true}]}""", """{"tokens":[],"finished":true}""")]
    // Text still provisional in the finishing message.
    [InlineData(true, """{"tokens":[{"text":"Hello","is_final":true}]}""", """{"tokens":[{"text":" world","is_final":false}],"finished":true}""")]
    public async Task IncompleteFinish_FaultsWithoutFinalTranscript(
        bool endOfAudioSent, string before, string finished)
    {
        var adapter = CreateAdapter();
        Handle(adapter, before);
        if (endOfAudioSent)
            await adapter.BeginFinalizeAsync(CancellationToken.None);

        var result = Handle(adapter, finished);

        AssertIncomplete(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetractedProvisionalText_DoesNotBlockCompletion(bool separateEmptyResponse)
    {
        var adapter = CreateAdapter();
        Handle(adapter, """{"tokens":[{"text":"Hello","is_final":true},{"text":" um","is_final":false}]}""");
        await adapter.BeginFinalizeAsync(CancellationToken.None);
        if (separateEmptyResponse)
            Assert.Null(Handle(adapter, """{"tokens":[]}""").Fault);

        var result = Handle(adapter, """{"tokens":[],"finished":true}""");

        Assert.Null(result.Fault);
        Assert.Equal(WebSocketSessionSignal.Terminal, result.Signals);
        Assert.Equal("Hello", Assert.Single(result.Transcripts).Text);
    }

    [Theory]
    [InlineData("""{"tokens":[{"text":" wor","is_final":false},{"text":"Hello","is_final":true}]}""")]
    [InlineData("""{"tokens":[{"text":1,"is_final":true}]}""")]
    [InlineData("""{"tokens":[{"text":"Hello"}]}""")]
    [InlineData("""{"tokens":[{"text":"Hello","is_final":"true"}]}""")]
    [InlineData("""{"tokens":["Hello"]}""")]
    [InlineData("""{"tokens":{"text":"Hello","is_final":true}}""")]
    [InlineData("""{"finished":false}""")]
    [InlineData("""{"tokens":[],"finished":"true"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void MalformedResponse_Faults(string json)
    {
        AssertIncomplete(Handle(CreateAdapter(), json));
    }

    [Fact]
    public void TranscriptBeyondLengthLimit_Faults()
    {
        var adapter = CreateAdapter();
        var chunk = new string('a', SonioxTranscriptAggregator.MaximumTranscriptLength / 2);
        var message = $$"""{"tokens":[{"text":"{{chunk}}","is_final":true}]}""";
        Assert.Null(Handle(adapter, message).Fault);
        Assert.Null(Handle(adapter, message).Fault);

        AssertIncomplete(Handle(adapter, """{"tokens":[{"text":"a","is_final":false}]}"""));
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(402, PluginRequestFailureKind.Permission)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(408, PluginRequestFailureKind.Timeout)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(400, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public void Error_IsClassifiedByCodeWithoutEchoingThePayload(int code, PluginRequestFailureKind kind)
    {
        var result = Handle(CreateAdapter(), $$"""
            {"tokens":[],"error_code":{{code}},"error_type":"some_type","error_message":"rejected",
             "request_id":"req-1","more_info":"echoed-secret"}
            """);

        Assert.Empty(result.Transcripts);
        var exception = Assert.IsType<PluginRequestException>(result.Fault);
        Assert.Equal(kind, exception.FailureKind);
        Assert.Equal(code, exception.HttpStatusCode);
        Assert.Equal("Soniox streaming error: some_type: rejected (request_id: req-1)", exception.Message);
    }

    [Theory]
    [InlineData("""{"error_code":401}""", PluginRequestFailureKind.Authentication, "Soniox streaming error: HTTP 401")]
    [InlineData("""{"error_code":"bad","note":"echoed-secret"}""", PluginRequestFailureKind.Unknown, "Soniox streaming error: request rejected")]
    [InlineData("""{"tokens":[],"error_message":"rejected"}""", PluginRequestFailureKind.Unknown, "Soniox streaming error: rejected")]
    public void ErrorWithoutAllFields_StillFaults(string json, PluginRequestFailureKind kind, string expected)
    {
        var exception = Assert.IsType<PluginRequestException>(Handle(CreateAdapter(), json).Fault);

        Assert.Equal(kind, exception.FailureKind);
        Assert.Equal(expected, exception.Message);
    }

    private static void AssertIncomplete(WebSocketInboundResult result)
    {
        Assert.Empty(result.Transcripts);
        Assert.Equal(WebSocketSessionSignal.None, result.Signals);
        var exception = Assert.IsType<PluginRequestException>(result.Fault);
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, exception.FailureKind);
    }

    private static async Task<WebSocketSessionPump> StartAsync(ScriptedWebSocketTransport transport)
    {
        var pump = await WebSocketSessionPump
            .StartConnectedAsync(CreateAdapter(), transport, CancellationToken.None)
            .WaitAsync(s_timeout);
        await transport.NextSentAsync();
        return pump;
    }

    private static SonioxWebSocketAdapter CreateAdapter() =>
        new("key", new Uri("wss://example.com/realtime"), []);

    private static WebSocketInboundResult Handle(SonioxWebSocketAdapter adapter, string json) =>
        adapter.HandleMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));
}
