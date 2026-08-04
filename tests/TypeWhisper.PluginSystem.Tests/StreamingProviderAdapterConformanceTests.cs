using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.Plugin.Deepgram;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.Plugin.Speechmatics;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StreamingProviderAdapterConformanceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    // Keyed by name, not a factory delegate: a non-serializable argument stops the
    // test runner from enumerating the rows individually.
    public static TheoryData<string> MigratedAdapters =>
        [
            "AssemblyAI",
            "Deepgram",
            "ElevenLabs",
            "Smallest AI",
            "Reson8",
        ];

    public static TheoryData<string> RemainingProviders =>
        [
            "Soniox",
            "Speechmatics",
            "Gladia",
            "xAI",
            "OpenAI",
        ];

    private static IWebSocketSessionAdapter CreateMigratedAdapter(string provider) =>
        provider switch
        {
            "AssemblyAI" => new AssemblyAiWebSocketAdapter("key", "en"),
            "Deepgram" => new DeepgramWebSocketAdapter("key", "nova-3", "en"),
            "ElevenLabs" => new ElevenLabsWebSocketAdapter(
                "key",
                "scribe_v2_realtime",
                "en"
            ),
            "Smallest AI" => new SmallestAiWebSocketAdapter("key", "en"),
            "Reson8" => new Reson8WebSocketAdapter(
                "key",
                "https://api.reson8.dev",
                "Authorization",
                null,
                "en"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

    [Theory]
    [MemberData(nameof(MigratedAdapters))]
    public async Task CloseBeforeDocumentedTerminal_Faults(string provider)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(
            CreateMigratedAdapter(provider),
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose(WebSocketCloseStatus.NormalClosure, "early");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.Contains("before", exception.Message);
        Assert.Contains(provider, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionOptions_EncodeExpectedUrisAndAuthentication()
    {
        var assembly = await new AssemblyAiWebSocketAdapter("assembly-key", "de")
            .GetConnectionOptionsAsync(CancellationToken.None);
        var deepgram = await new DeepgramWebSocketAdapter(
                "deepgram-key",
                "nova-3",
                null
            )
            .GetConnectionOptionsAsync(CancellationToken.None);
        var eleven = await new ElevenLabsWebSocketAdapter(
                "eleven-key",
                "scribe_v2_realtime",
                "de"
            )
            .GetConnectionOptionsAsync(CancellationToken.None);
        var smallest = await new SmallestAiWebSocketAdapter("smallest-key", "de")
            .GetConnectionOptionsAsync(CancellationToken.None);
        var reson8 = await new Reson8WebSocketAdapter(
                "reson-key",
                "https://api.reson8.dev/base",
                "X-Api-Key",
                "domain",
                "de"
            )
            .GetConnectionOptionsAsync(CancellationToken.None);
        using var gladiaHttp = new HttpClient(
            new JsonResponseHandler(
                """{"url":"wss://api.gladia.io/v2/live?token=test-token"}"""
            )
        );
        var gladia = await new GladiaWebSocketAdapter(
                gladiaHttp,
                "gladia-key",
                "de"
            )
            .GetConnectionOptionsAsync(CancellationToken.None);
        var soniox = await new SonioxWebSocketAdapter("soniox-key", "de")
            .GetConnectionOptionsAsync(CancellationToken.None);
        var speechmatics = await new SpeechmaticsWebSocketAdapter(
                "speechmatics-key",
                "de"
            )
            .GetConnectionOptionsAsync(CancellationToken.None);
        var xai = await new XaiWebSocketAdapter("xai-key", "de")
            .GetConnectionOptionsAsync(CancellationToken.None);
        var openAi = await new OpenAiRealtimeWebSocketAdapter(
                "openai-key",
                "de",
                null,
                useServerVad: true,
                sendSessionUpdate: true
            )
            .GetConnectionOptionsAsync(CancellationToken.None);

        Assert.Equal("assembly-key", assembly.Headers!["Authorization"]);
        Assert.Contains("speech_model=universal-streaming-multilingual", assembly.Uri.Query);
        Assert.Equal("Token deepgram-key", deepgram.Headers!["Authorization"]);
        Assert.Contains("language=multi", deepgram.Uri.Query);
        Assert.Equal("eleven-key", eleven.Headers!["xi-api-key"]);
        Assert.Contains("include_timestamps=true", eleven.Uri.Query);
        Assert.Equal("Bearer smallest-key", smallest.Headers!["Authorization"]);
        Assert.Contains("language=de", smallest.Uri.Query);
        Assert.Equal("reson-key", reson8.Headers!["X-Api-Key"]);
        Assert.Equal("/base/v1/speech-to-text/realtime", reson8.Uri.AbsolutePath);
        Assert.Equal("test-token", ParseQuery(gladia.Uri)["token"]);
        Assert.Equal("stt-rt.soniox.com", soniox.Uri.Host);
        Assert.Equal(
            "Bearer speechmatics-key",
            speechmatics.Headers!["Authorization"]
        );
        Assert.Equal("Bearer xai-key", xai.Headers!["Authorization"]);
        Assert.Equal("Bearer openai-key", openAi.Headers!["Authorization"]);
    }

    [Fact]
    public async Task SmallestAi_EmptyIsLastAcknowledgement_CompletesFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(
            new SmallestAiWebSocketAdapter("key", null),
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(
            """{"type":"transcription","status":"success","transcript":"","is_final":true,"is_last":true}"""
        );

        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task Reson8_OnlyMatchingFlushIdCompletesFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(
            new Reson8WebSocketAdapter(
                "key",
                "https://api.reson8.dev",
                "Authorization",
                null,
                null
            ),
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var flush = await transport.NextSentAsync();
        using var document = JsonDocument.Parse(flush.Payload);
        var id = document.RootElement.GetProperty("id").GetString();

        transport.EnqueueText(
            """{"type":"flush_confirmation","id":"different-request"}"""
        );
        await Task.Delay(50);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText(
            JsonSerializer.Serialize(
                new { type = "flush_confirmation", id }
            )
        );
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public Task Reson8_CloseBeforeMatchingFlushConfirmation_Faults() =>
        AssertCloseBeforeTerminalFailsAsync(
            new Reson8WebSocketAdapter(
                "key",
                "https://api.reson8.dev",
                "Authorization",
                null,
                null
            )
        );

    [Fact]
    public Task SmallestAi_CloseBeforeIsLast_Faults() =>
        AssertCloseBeforeTerminalFailsAsync(
            new SmallestAiWebSocketAdapter("key", null)
        );

    [Fact]
    public async Task ElevenLabs_CommittedEventVariants_AreDeduplicated()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(
            new ElevenLabsWebSocketAdapter("key", "scribe_v2_realtime", null),
            transport
        );
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        var marker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        pump.TranscriptReceived += transcript =>
        {
            events.Enqueue(transcript);
            if (transcript.Text == "marker")
                marker.TrySetResult();
        };

        transport.EnqueueText(
            """{"message_type":"committed_transcript","text":"One segment."}"""
        );
        transport.EnqueueText(
            """{"message_type":"committed_transcript_with_timestamps","text":"One segment.","words":[]}"""
        );
        transport.EnqueueText(
            """{"message_type":"partial_transcript","text":"marker"}"""
        );
        await marker.Task.WaitAsync(s_timeout);

        Assert.Single(
            events,
            item => item == new StreamingTranscriptEvent("One segment.", true)
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(
            """{"message_type":"committed_transcript","text":""}"""
        );
        await finalize.WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData("AssemblyAI")]
    [InlineData("Deepgram")]
    [InlineData("ElevenLabs")]
    [InlineData("SmallestAI")]
    [InlineData("Reson8")]
    public async Task ExplicitProviderError_FaultsPump(string provider)
    {
        var (adapter, errorJson) = provider switch
        {
            "AssemblyAI" => (
                (IWebSocketSessionAdapter)new AssemblyAiWebSocketAdapter("key", null),
                """{"type":"Error","error":"rejected"}"""
            ),
            "Deepgram" => (
                new DeepgramWebSocketAdapter("key", "nova-3", null),
                """{"type":"Error","description":"rejected"}"""
            ),
            "ElevenLabs" => (
                new ElevenLabsWebSocketAdapter("key", "scribe_v2_realtime", null),
                """{"message_type":"auth_error","message":"rejected"}"""
            ),
            "SmallestAI" => (
                new SmallestAiWebSocketAdapter("key", null),
                """{"type":"error","message":"rejected"}"""
            ),
            _ => (
                new Reson8WebSocketAdapter(
                    "key",
                    "https://api.reson8.dev",
                    "Authorization",
                    null,
                    null
                ),
                """{"type":"provider_error","message":"rejected"}"""
            ),
        };
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(adapter, transport);

        transport.EnqueueText(errorJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pump.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout)
        );
        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(RemainingProviders))]
    public async Task RemainingProvider_CloseBeforeDocumentedTerminal_Faults(
        string provider
    )
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            provider,
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose(WebSocketCloseStatus.NormalClosure, "early");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.Contains("before", exception.Message);
    }

    [Theory]
    [MemberData(nameof(RemainingProviders))]
    public async Task RemainingProvider_ExplicitErrorFaultsPump(string provider)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            provider,
            transport
        );
        var error = provider switch
        {
            "Soniox" => """{"error_message":"rejected"}""",
            "Speechmatics" => """{"message":"Error","reason":"rejected"}""",
            "Gladia" => """{"type":"error","message":"rejected"}""",
            // xAI and OpenAI both nest the message under "error".
            _ => """{"type":"error","error":{"message":"rejected"}}""",
        };

        transport.EnqueueText(error);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pump.FinalizeAsync(CancellationToken.None).WaitAsync(s_timeout)
        );
        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Soniox_CloseBeforeFinished_DoesNotSynthesizeFinalTranscript()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "Soniox",
            transport
        );
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += events.Enqueue;
        transport.EnqueueText(
            """{"tokens":[{"text":"Hello","is_final":true}],"finished":false}"""
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.DoesNotContain(events, transcript => transcript.IsFinal);
    }

    [Fact]
    public async Task Speechmatics_CloseBeforeEndOfTranscript_DoesNotSynthesizeFinal()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "Speechmatics",
            transport
        );
        var events = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += events.Enqueue;
        transport.EnqueueText(
            """{"message":"AddTranscript","metadata":{"transcript":"Hello"}}"""
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
        Assert.DoesNotContain(events, transcript => transcript.IsFinal);
    }

    [Fact]
    public async Task Gladia_EndRecordingDoesNotComplete_EndSessionDoes()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "Gladia",
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"type":"end_recording"}""");
        await Task.Delay(50);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText("""{"type":"end_session"}""");
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task Gladia_CloseBeforeEndSession_Faults()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "Gladia",
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
    }

    [Fact]
    public async Task Speechmatics_FinalizationUsesSerializedAudioSequenceNumber()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "Speechmatics",
            transport
        );

        await pump
            .SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None)
            .WaitAsync(s_timeout);
        await transport.NextSentAsync();
        await pump
            .SendAudioAsync(new byte[] { 3, 4 }, CancellationToken.None)
            .WaitAsync(s_timeout);
        await transport.NextSentAsync();
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var end = await transport.NextSentAsync();
        using var document = JsonDocument.Parse(end.Payload);

        Assert.Equal(2, document.RootElement.GetProperty("last_seq_no").GetInt32());
        transport.EnqueueText("""{"message":"EndOfTranscript"}""");
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task OpenAi_FinalizationWaitsForMatchingCommittedItem()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "OpenAI",
            transport
        );

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(
            """{"type":"input_audio_buffer.committed","item_id":"tail"}"""
        );
        transport.EnqueueText(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"different","transcript":"old"}"""
        );
        await Task.Delay(50);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"tail","transcript":"tail"}"""
        );
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task OpenAi_FinalizationWaitsForEarlierItemCompletingOutOfOrder()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAdditionalProviderAsync(
            "OpenAI",
            transport
        );

        var observed = Channel.CreateUnbounded<StreamingTranscriptEvent>();
        pump.TranscriptReceived += evt => observed.Writer.TryWrite(evt);

        // Server VAD commits an earlier utterance still transcribing. The delta is only a
        // sync point: the receive loop is sequential, so observing it proves the commit
        // landed before we send audio.
        transport.EnqueueText(
            """{"type":"input_audio_buffer.committed","item_id":"earlier"}"""
        );
        transport.EnqueueText(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"earlier","delta":"partial"}"""
        );
        var partial = await observed.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
        Assert.Equal(new StreamingTranscriptEvent("partial", false), partial);

        // Fresh audio after that commit is what makes finalize issue an explicit
        // commit -- the event that designates the final item.
        await pump.SendAudioAsync(new byte[1600], CancellationToken.None)
            .WaitAsync(s_timeout);
        transport.DrainSent();

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var commit = await transport.NextSentAsync();
        Assert.Contains(
            "input_audio_buffer.commit",
            Encoding.UTF8.GetString(commit.Payload.Span)
        );
        transport.EnqueueText(
            """{"type":"input_audio_buffer.committed","item_id":"tail"}"""
        );

        // Tail completes first; the earlier item is still outstanding, so finalize
        // must not complete yet.
        transport.EnqueueText(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"tail","transcript":"tail."}"""
        );
        var tail = await observed.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
        Assert.Equal(new StreamingTranscriptEvent("tail.", true), tail);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"earlier","transcript":"Earlier utterance."}"""
        );
        var earlier = await observed.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
        Assert.Equal(new StreamingTranscriptEvent("Earlier utterance.", true), earlier);
        await finalize.WaitAsync(s_timeout);
    }

    private static Task<WebSocketSessionPump> StartAsync(
        IWebSocketSessionAdapter adapter,
        ScriptedWebSocketTransport transport
    ) =>
        WebSocketSessionPump
            .StartConnectedAsync(adapter, transport, CancellationToken.None)
            .WaitAsync(s_timeout);

    // A start that faults disposes the transport, and disposal completes its sent channel — so
    // awaiting the first send reports "the channel has been closed" and buries the reason the
    // start failed. Surface the start's own exception whenever it has one.
    private static async Task FirstSentOrStartFailureAsync(
        Task<WebSocketSessionPump> starting,
        ScriptedWebSocketTransport transport
    )
    {
        try
        {
            await transport.NextSentAsync();
        }
        catch (ChannelClosedException)
        {
            await starting.WaitAsync(s_timeout);
            throw;
        }
    }

    private static async Task<WebSocketSessionPump> StartAdditionalProviderAsync(
        string provider,
        ScriptedWebSocketTransport transport
    )
    {
        IWebSocketSessionAdapter adapter = provider switch
        {
            "Soniox" => new SonioxWebSocketAdapter("key", null),
            // Speechmatics rejects a null language at StartRecognition; passing null made
            // every Speechmatics case fault during start and tear the transport down, which
            // surfaced as "the channel has been closed" from the first NextSentAsync.
            "Speechmatics" => new SpeechmaticsWebSocketAdapter("key", "en"),
            "Gladia" => new GladiaWebSocketAdapter(new HttpClient(), "key", null),
            "xAI" => new XaiWebSocketAdapter("key", null),
            _ => new OpenAiRealtimeWebSocketAdapter(
                "key",
                null,
                null,
                useServerVad: true,
                sendSessionUpdate: false
            ),
        };

        // Speechmatics and xAI gate readiness on a provider signal, so they need it
        // scripted while start is still in flight -- the shared StartAsync can't do that.
        switch (provider)
        {
            case "Speechmatics":
            {
                var starting = WebSocketSessionPump.StartConnectedAsync(
                    adapter,
                    transport,
                    CancellationToken.None
                );
                await FirstSentOrStartFailureAsync(starting, transport);
                transport.EnqueueText("""{"message":"RecognitionStarted"}""");
                return await starting.WaitAsync(s_timeout);
            }
            case "xAI":
            {
                var starting = WebSocketSessionPump.StartConnectedAsync(
                    adapter,
                    transport,
                    CancellationToken.None
                );
                transport.EnqueueText("""{"type":"transcript.created"}""");
                return await starting.WaitAsync(s_timeout);
            }
        }

        var pump = await StartAsync(adapter, transport);
        switch (provider)
        {
            case "Soniox":
                await transport.NextSentAsync();
                break;
            case "OpenAI":
                await pump
                    .SendAudioAsync(new byte[] { 1, 0 }, CancellationToken.None)
                    .WaitAsync(s_timeout);
                await transport.NextSentAsync();
                break;
        }

        return pump;
    }

    private static async Task AssertCloseBeforeTerminalFailsAsync(
        IWebSocketSessionAdapter adapter
    )
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(adapter, transport);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueClose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalize.WaitAsync(s_timeout)
        );
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part[1])
            );

    private sealed class JsonResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
    }
}
