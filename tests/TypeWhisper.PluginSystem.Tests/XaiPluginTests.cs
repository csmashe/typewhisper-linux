using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing 'request' (method, URI,
// headers) and return a canned response. ReSharper reads xUnit asserts as
// precondition checks and concludes 'request' is only validated, never used —
// but asserting on the request is exactly what these tests verify, so the
// inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class XaiPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new XaiPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesXaiPluginIdentity()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.xai", manifest.GetProperty("id").GetString());
        Assert.Equal("xAI / Grok", manifest.GetProperty("name").GetString());
        Assert.Equal(["transcription", "llm", "tts"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            "TypeWhisper.Plugin.Xai.dll",
            manifest.GetProperty("assemblyName").GetString()
        );
        Assert.Equal(
            "TypeWhisper.Plugin.Xai.XaiPlugin",
            manifest.GetProperty("pluginClass").GetString()
        );
    }

    [Fact]
    public async Task ActivateAsync_RestoresDefaultsAndExposesAllProviderCapabilities()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };

        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.xai", sut.PluginId);
        Assert.Equal("xAI / Grok", sut.PluginName);
        Assert.Equal("xai", sut.ProviderId);
        Assert.Equal("xAI / Grok", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.IsAvailable);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("grok-stt", sut.SelectedModelId);
        Assert.Equal(["grok-stt"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("grok-4.3", sut.SelectedLlmModelId);
        Assert.Equal(["grok-4.3"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("eve", sut.SelectedVoiceId);
        Assert.Equal(["eve", "ara", "leo", "rex", "sal"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Contains("Eve", sut.SettingsSummary);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("xai-key");
        await sut.SetApiKeyAsync("xai-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FiltersAndSortsXaiModelResults()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.x.ai/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());

            return JsonResponse("""
                {
                  "data": [
                    { "id": "grok-stt", "owned_by": "xai" },
                    { "id": "grok-4.3", "owned_by": "xai" },
                    { "id": "grok-imagine-image", "owned_by": "xai" },
                    { "id": "grok-4.20-0309-non-reasoning", "owned_by": "xai" },
                    { "id": "voice-agent", "owned_by": "xai" },
                    { "id": "embedding-model", "owned_by": "xai" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.Equal(["grok-4.20-0309-non-reasoning", "grok-4.3"], models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ProcessAsync_UsesResponsesApiAndSelectedModel()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/responses", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            var root = doc.RootElement;
            Assert.Equal("grok-4.3", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("system", root.GetProperty("input")[0].GetProperty("role").GetString());
            Assert.Equal("user", root.GetProperty("input")[1].GetProperty("role").GetString());

            return JsonResponse("""{ "output_text": "Cleaned transcript" }""");
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("Cleaned transcript", result);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsResponsesApiDeltasInOrder()
    {
        string? capturedBody = null;
        var sse = string.Join(
            "\n",
            "data: {\"type\":\"response.created\"}",
            "",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}",
            "",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}",
            "",
            "data: {\"type\":\"response.completed\"}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedBody = body;
            Assert.Equal("https://api.x.ai/v1/responses", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("grok-4.3", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "output_text": "bulk" }"""));

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    [Theory]
    [InlineData("""{"type":"response.output_text.delta","delta":"hi"}""", "hi")]
    [InlineData("""{"type":"response.output_text.done","text":"ignored"}""", null)]
    [InlineData("""{"type":"response.created"}""", null)]
    [InlineData("not json", null)]
    public void XaiResponsesClient_ParseStreamDelta_ExtractsOnlyDeltaFrames(string payload, string? expected)
    {
        Assert.Equal(expected, XaiResponsesClient.ParseStreamDelta(payload));
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"message":"boom"}}""", "boom")]
    [InlineData("""{"type":"error","message":"top-level boom"}""", "top-level boom")]
    [InlineData("""{"type":"response.failed","response":{"error":{"message":"failed badly"}}}""", "failed badly")]
    [InlineData("""{"type":"response.output_text.delta","delta":"hi"}""", null)]
    [InlineData("""{"type":"response.completed"}""", null)]
    [InlineData("not json", null)]
    public void XaiResponsesClient_ParseStreamError_DetectsFailureFrames(string payload, string? expected)
    {
        Assert.Equal(expected, XaiResponsesClient.ParseStreamError(payload));
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsOnResponseFailedFrameAfterPartialDeltas()
    {
        // A Responses stream returns 200 then can fail mid-flight via a typed
        // frame. The reader must throw so LlmStreamPump faults and the caller
        // falls back to batch, rather than committing the partial deltas as success.
        var sse = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}",
            "",
            "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"server overloaded\"}}}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
                chunks.Add(chunk);
        });

        Assert.Equal(["Hel"], chunks);
        Assert.Contains("server overloaded", ex.Message);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsWhenEofPrecedesResponseCompleted()
    {
        var sse = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<IncompleteSseStreamException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["partial"], chunks);
        Assert.Equal("xAI stream", ex.StreamName);
        Assert.Equal("response.completed", ex.ExpectedTerminal);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsWhenDonePrecedesResponseCompleted()
    {
        var sse = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<IncompleteSseStreamException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["partial"], chunks);
        Assert.Equal("xAI stream", ex.StreamName);
        Assert.Equal("response.completed", ex.ExpectedTerminal);
    }

    [Theory]
    [InlineData(
        """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}""",
        "max_output_tokens")]
    [InlineData(
        """{"type":"response.cancelled","response":{"status":"cancelled","error":{"message":"cancelled upstream"}}}""",
        "cancelled upstream")]
    [InlineData(
        """{"type":"response.canceled","response":{"status":"canceled"}}""",
        "canceled")]
    [InlineData(
        """{"type":"response.completed","response":{"status":"cancelled"}}""",
        "cancelled")]
    public async Task ProcessStreamingAsync_ThrowsOnIncompleteOrCancelledTerminalFrame(
        string terminalPayload,
        string expectedDetail)
    {
        var sse = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}",
            "",
            $"data: {terminalPayload}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["partial"], chunks);
        Assert.Contains(expectedDetail, ex.Message);
    }

    [Fact]
    public void XaiResponsesClient_ParseResponse_ExtractsNestedOutputText()
    {
        var result = XaiResponsesClient.ParseResponse("""
            {
              "output": [
                { "type": "reasoning", "status": "completed" },
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "Nested " },
                    { "type": "text", "text": "response" }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("Nested response", result);
    }

    [Fact]
    public async Task TranscribeAsync_PostsWavToSttEndpointWithLanguageFormatFields()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/stt", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            Assert.NotNull(body);
            Assert.True(body.Contains("name=\"format\"", StringComparison.Ordinal)
                || body.Contains("name=format", StringComparison.Ordinal));
            Assert.Contains("true", body);
            Assert.True(body.Contains("name=\"language\"", StringComparison.Ordinal)
                || body.Contains("name=language", StringComparison.Ordinal));
            Assert.Contains("de", body);
            Assert.True(body.Contains("name=\"file\"; filename=\"audio.wav\"", StringComparison.Ordinal)
                || body.Contains("name=file; filename=audio.wav", StringComparison.Ordinal));

            return JsonResponse("""
                {
                  "text": "Hallo Welt",
                  "language": "German",
                  "duration": 1.25,
                  "words": [
                    { "text": "Hallo", "start": 0.0, "end": 0.5 },
                    { "text": "Welt", "start": 0.5, "end": 1.25 }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("German", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal(["Hallo", "Welt"], result.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: true, prompt: null, CancellationToken.None));

        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public void StreamingSession_BuildsExpectedUriAndExposesAuthHeader()
    {
        var uri = XaiStreamingSession.BuildStreamingUri("de", interimResults: true);
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("api.x.ai", uri.Host);
        Assert.Equal("/v1/stt", uri.AbsolutePath);
        Assert.Contains("sample_rate=16000", uri.Query);
        Assert.Contains("encoding=pcm", uri.Query);
        Assert.Contains("interim_results=true", uri.Query);
        Assert.Contains("language=de", uri.Query);

        var headers = XaiStreamingSession.CreateStreamingHeaders("xai-key");
        Assert.Equal("Bearer xai-key", headers["Authorization"]);
    }

    [Fact]
    public async Task StreamingSession_SendAudioWaitsForTranscriptCreated()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session =
            XaiStreamingSession.CreateConnectedSessionForTests(socket);

        var sendTask = session.SendAudioAsync(
            new byte[] { 1, 2, 3, 4 },
            CancellationToken.None);

        Assert.False(sendTask.IsCompleted);
        Assert.Empty(socket.SentFrames);

        socket.EnqueueText("""{"type":"transcript.created"}""");

        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        var sent = Assert.Single(socket.SentFrames);
        Assert.Equal(WebSocketMessageType.Binary, sent.MessageType);
        Assert.Equal([1, 2, 3, 4], sent.Payload);
    }

    [Fact]
    public async Task StreamingSession_DisposeWithStuckHandshake_CancelsStartupAndReleasesTheSocket()
    {
        // The peer never sends transcript.created and never closes, so nothing but disposal can
        // end the handshake. Disposal must actually tear the socket down, not just stop waiting.
        var socket = new FakeStreamingWebSocket();
        var session = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            disposePumpWait: TimeSpan.FromMilliseconds(200)
        );

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.DisposeCalled);
        Assert.NotEqual(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task StreamingSession_ConnectedFactoryWaitsForTranscriptCreated()
    {
        var socket = new FakeStreamingWebSocket();
        var connectTask = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(connectTask.IsCompleted);

        socket.EnqueueText("""{"type":"transcript.created"}""");

        await using var session = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Theory]
    [InlineData(false, "before transcript.created")]
    [InlineData(true, "quota exceeded")]
    public async Task StreamingSession_CloseOrErrorBeforeReadinessFaultsConnect(
        bool providerError,
        string expectedMessage)
    {
        var socket = new FakeStreamingWebSocket();
        var connectTask = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        if (providerError)
        {
            socket.EnqueueText(
                """{"type":"error","error":{"message":"quota exceeded"}}""");
        }
        else
        {
            socket.EnqueueClose(
                WebSocketCloseStatus.EndpointUnavailable,
                "provider unavailable");
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.True(socket.AbortCalled);
        Assert.True(socket.DisposeCalled);
    }

    [Fact]
    public async Task StreamingSession_CallerCancellationDuringReadinessWaitIsCleanAndTearsDown()
    {
        using var startupCts = new CancellationTokenSource();
        var socket = new FakeStreamingWebSocket();
        var connectTask = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            TimeSpan.FromSeconds(5),
            startupCts.Token);

        // ReSharper disable once MethodHasAsyncOverload -- the assertion requires cancellation to be observable immediately.
        startupCts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            // ReSharper disable once MethodSupportsCancellation -- must not pass startupCts.Token: it is already canceled here, so WaitAsync would throw before connectTask propagates its own cancellation, hollowing out the token assertion below. The TimeSpan is only a hang guard.
            async () => await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(startupCts.Token, exception.CancellationToken);
        Assert.True(socket.AbortCalled);
        Assert.True(socket.DisposeCalled);
        Assert.True(socket.ReceiveExited.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StreamingSession_ReadinessTimeoutFaultsAndTearsDown()
    {
        var socket = new FakeStreamingWebSocket();
        var connectTask = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("transcript.created", exception.Message);
        Assert.True(socket.AbortCalled);
        Assert.True(socket.DisposeCalled);
        Assert.True(socket.ReceiveExited.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StreamingSession_CloseBeforeTranscriptDoneFaultsFinalize()
    {
        // Regression: after transcript.created the readiness signal is already
        // completed, so a graceful Close frame arriving before transcript.done
        // must still be recorded as a session fault. Otherwise FinalizeAsync
        // returns cleanly and the coordinator commits the partial transcript
        // as success instead of falling back to the complete-WAV batch path,
        // silently truncating dictation.
        var socket = new FakeStreamingWebSocket();
        var connectTask = XaiStreamingSession.CreateConnectedSessionForTests(
            socket,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        socket.EnqueueText("""{"type":"transcript.created"}""");
        await using var session = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));

        // A final segment lands, then FinalizeAsync parks on the terminal wait
        // (socket still open) before the server closes mid-stream — no
        // transcript.done ever arrives.
        socket.EnqueueText(
            """{"type":"transcript.partial","text":"hello","is_final":true,"speech_final":false}""");
        var finalizeTask = session.FinalizeAsync(CancellationToken.None);
        socket.EnqueueClose(
            WebSocketCloseStatus.EndpointUnavailable,
            "mid-stream disconnect");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await finalizeTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("faulted", exception.Message);
        Assert.Contains("transcript.done", exception.Message);
    }

    [Fact]
    public void TranscriptCollector_EmitsPerSegmentDeltasAndSuppressesCumulativeFinals()
    {
        // Coordinator contract: every IsFinal=true event appends to
        // _finalSegments. Cumulative final text would double-append, so the
        // collector must emit segment deltas only.
        var collector = new XaiTranscriptCollector();

        Assert.Null(collector.ApplyEvent("""{"type":"transcript.created"}"""));

        // Non-final partial: interim text passes through.
        var interim = collector.ApplyEvent("""{"type":"transcript.partial","text":"hello","is_final":false,"speech_final":false}""");
        Assert.NotNull(interim);
        Assert.Equal("hello", interim.Text);
        Assert.False(interim.IsFinal);

        // First final segment.
        var seg1 = collector.ApplyEvent("""{"type":"transcript.partial","text":"hello world","is_final":true,"speech_final":false}""");
        Assert.NotNull(seg1);
        Assert.Equal("hello world", seg1.Text);
        Assert.True(seg1.IsFinal);

        // Second final segment: emits the new segment as a delta (not cumulative).
        var seg2 = collector.ApplyEvent("""{"type":"transcript.partial","text":"how are you","is_final":true,"speech_final":false}""");
        Assert.NotNull(seg2);
        Assert.Equal("how are you", seg2.Text);

        // speech_final=true after per-segment finals is xAI's cumulative
        // summary — must be suppressed to avoid re-appending.
        var summary = collector.ApplyEvent("""{"type":"transcript.partial","text":"hello world how are you","is_final":true,"speech_final":true}""");
        Assert.Null(summary);

        // transcript.done after finals is also cumulative; suppress.
        var done = collector.ApplyEvent("""{"type":"transcript.done","text":"hello world how are you","language":"en","duration":1.25}""");
        Assert.Null(done);

        // FinalResult (used outside the coordinator path) reflects metadata + done text.
        var final = collector.FinalResult("en");
        Assert.Equal("hello world how are you", final.Text);
        Assert.Equal("en", final.DetectedLanguage);
        Assert.Equal(1.25, final.DurationSeconds);
    }

    [Fact]
    public void TranscriptCollector_DoneEmitsSuffixWhenItExtendsPriorFinals()
    {
        // Regression: previously the done event was suppressed any time
        // per-segment finals had arrived, dropping a trailing utterance that
        // xAI only finalized via transcript.done. Now the done text is
        // checked against the joined finals — exact match suppresses, but a
        // strict extension (joined + " " + suffix) emits the suffix as a
        // delta so the coordinator captures the tail.
        var collector = new XaiTranscriptCollector();

        collector.ApplyEvent("""{"type":"transcript.partial","text":"hello","is_final":true,"speech_final":false}""");

        var done = collector.ApplyEvent("""{"type":"transcript.done","text":"hello world","language":"en","duration":0.5}""");
        Assert.NotNull(done);
        Assert.Equal("world", done.Text);
        Assert.True(done.IsFinal);
    }

    [Fact]
    public void TranscriptCollector_DoneSuppressesExactCumulative()
    {
        // The other half of the extends-prior-finals rule: when done text
        // matches the joined finals exactly, it's a redundant summary and
        // must NOT re-emit (would double-append).
        var collector = new XaiTranscriptCollector();

        collector.ApplyEvent("""{"type":"transcript.partial","text":"hello","is_final":true,"speech_final":false}""");
        collector.ApplyEvent("""{"type":"transcript.partial","text":"world","is_final":true,"speech_final":false}""");

        var done = collector.ApplyEvent("""{"type":"transcript.done","text":"hello world","language":"en","duration":0.5}""");
        Assert.Null(done);
    }

    [Fact]
    public void TranscriptCollector_DoneEmitsAsFinalWhenNoSegmentFinalsArrived()
    {
        // Edge case: xAI sends transcript.done without preceding per-segment
        // finals. The done text must reach the coordinator as a single final.
        var collector = new XaiTranscriptCollector();

        Assert.Null(collector.ApplyEvent("""{"type":"transcript.created"}"""));

        var done = collector.ApplyEvent("""{"type":"transcript.done","text":"single shot transcript","language":"en","duration":0.5}""");
        Assert.NotNull(done);
        Assert.Equal("single shot transcript", done.Text);
        Assert.True(done.IsFinal);
    }

    [Fact]
    public void TranscriptCollector_IsTerminalFlipsOnDoneEvent()
    {
        // The session uses this to unblock FinalizeAsync as soon as xAI
        // declares the stream complete.
        var collector = new XaiTranscriptCollector();
        Assert.False(collector.IsTerminal);

        collector.ApplyEvent("""{"type":"transcript.partial","text":"hello","is_final":true,"speech_final":false}""");
        Assert.False(collector.IsTerminal);

        collector.ApplyEvent("""{"type":"transcript.done","text":"hello","language":"en","duration":0.5}""");
        Assert.True(collector.IsTerminal);
    }

    [Fact]
    public void TranscriptCollector_SpeechFinalOnFreshSegmentIsNotSuppressed()
    {
        // speech_final=true alone is NOT a cumulative-summary signal — it's
        // just an end-of-utterance marker that can appear on a normal final
        // segment. The cumulative-summary suppression only fires when the
        // text actually starts with the joined existing finals.
        var collector = new XaiTranscriptCollector();

        var first = collector.ApplyEvent("""{"type":"transcript.partial","text":"first","is_final":true,"speech_final":false}""");
        Assert.NotNull(first);
        Assert.Equal("first", first.Text);

        var second = collector.ApplyEvent("""{"type":"transcript.partial","text":"second","is_final":true,"speech_final":true}""");
        Assert.NotNull(second);
        Assert.Equal("second", second.Text);
        Assert.True(second.IsFinal);
    }

    [Fact]
    public void TranscriptCollector_SpeechFinalPrefixWithoutWordBoundaryIsNotSuppressed()
    {
        // Regression: bare StartsWith would suppress a fresh segment that
        // happens to begin with the same letters as the previous final
        // (e.g. previous "I", next "I'm here"). The cumulative-summary
        // suppression must require the prefix to end on a word boundary.
        var collector = new XaiTranscriptCollector();

        var first = collector.ApplyEvent("""{"type":"transcript.partial","text":"I","is_final":true,"speech_final":false}""");
        Assert.Equal("I", first?.Text);

        var second = collector.ApplyEvent("""{"type":"transcript.partial","text":"I'm here","is_final":true,"speech_final":true}""");
        Assert.NotNull(second);
        Assert.Equal("I'm here", second.Text);
        Assert.True(second.IsFinal);
    }

    [Fact]
    public void TranscriptCollector_PreservesRepeatedFinalSegments()
    {
        // A user genuinely saying the same word twice (e.g. "yes" then "yes")
        // produces two finalized segments with identical text. Both must
        // reach the coordinator — exact-text alone is not a retransmission
        // signal, only cumulative speech_final=true after segment finals is.
        var collector = new XaiTranscriptCollector();

        var first = collector.ApplyEvent("""{"type":"transcript.partial","text":"yes","is_final":true,"speech_final":false}""");
        Assert.NotNull(first);
        Assert.Equal("yes", first.Text);

        var second = collector.ApplyEvent("""{"type":"transcript.partial","text":"yes","is_final":true,"speech_final":false}""");
        Assert.NotNull(second);
        Assert.Equal("yes", second.Text);
        Assert.True(second.IsFinal);
    }

    [Fact]
    public void TranscriptCollector_ThrowsOnProviderErrorEvent()
    {
        // Receive loop relies on this throw to capture _receiveLoopException
        // and surface it on the next SendAudioAsync / FinalizeAsync call —
        // the coordinator's sender then faults and triggers batch fallback.
        var collector = new XaiTranscriptCollector();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            collector.ApplyEvent("""{"type":"error","error":{"message":"quota exceeded"}}"""));
        Assert.Contains("quota exceeded", ex.Message);

        var bareEx = Assert.Throws<InvalidOperationException>(() =>
            collector.ApplyEvent("""{"type":"error"}"""));
        Assert.Contains("Unknown xAI STT error", bareEx.Message);
    }

    [Fact]
    public async Task StartStreamingAsync_ThrowsWhenNotConfigured()
    {
        var host = new TestPluginHostServices();
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.StartStreamingAsync(language: null, CancellationToken.None));

        Assert.Contains("Settings.NotConfiguredApiKeyRequired", ex.Message);
    }

    [Fact]
    public async Task FetchVoicesAsync_ParsesVoiceListAndPersistsSelectedVoice()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.x.ai/v1/tts/voices", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "voices": [
                    { "voice_id": "rex", "name": "Rex", "language": "multilingual" },
                    { "voice_id": "ara", "name": "Ara", "language": "multilingual" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var voices = await sut.FetchVoicesAsync();
        sut.SetFetchedVoices(voices);
        sut.SelectVoice("rex");
        sut.SetCustomVoiceId("custom-voice");

        Assert.Equal(["ara", "rex"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal("custom-voice", sut.SelectedVoiceId);
        Assert.Equal("rex", host.GetSetting<string>("selectedVoice"));
        Assert.Equal("custom-voice", host.GetSetting<string>("customVoiceId"));
    }

    [Fact]
    public async Task SpeakAsync_PostsPcmTtsRequestAndUsesHostPcmPlayback()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/tts", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            var root = doc.RootElement;
            Assert.Equal("Read this", root.GetProperty("text").GetString());
            Assert.Equal("rex", root.GetProperty("voice_id").GetString());
            Assert.Equal("de", root.GetProperty("language").GetString());
            Assert.Equal("pcm", root.GetProperty("output_format").GetProperty("codec").GetString());
            Assert.Equal(24000, root.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            Assert.Equal(1, root.GetProperty("optimize_streaming_latency").GetInt32());
            Assert.True(root.GetProperty("text_normalization").GetBoolean());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 1, 2, 3]),
            };
        });

        var playback = new RecordingPcmPlaybackService();
        var host = new TestPluginHostServices
        {
            PcmPlayback = playback,
            Secrets = { ["api-key"] = "xai-key" },
        };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SelectVoice("rex");
        sut.SetTtsLowLatency(true);
        sut.SetTtsTextNormalization(true);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Read this", "de"), CancellationToken.None);

        Assert.NotNull(session);
        var playbackRequest = Assert.Single(playback.Requests);
        Assert.Equal([0, 1, 2, 3], playbackRequest.Payload.ToArray());
        Assert.Equal(24_000, playbackRequest.SampleRate);
        Assert.Equal(1, playbackRequest.Channels);
        Assert.Equal(PcmSampleFormat.Signed16LittleEndian, playbackRequest.Format);
    }

    [Fact]
    public async Task SpeakAsync_SkipsNetworkRequestWhenNoPlayerAvailable()
    {
        var requestCount = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 1, 2, 3]),
            };
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Read this", "en"), CancellationToken.None);

        Assert.False(session.IsActive);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task GetSettingDefinitions_ExposesApiKeyModelsVoiceAndTtsToggles()
    {
        var host = new TestPluginHostServices();
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        var keys = sut.GetSettingDefinitions().Select(d => d.Key).ToArray();

        Assert.Equal(
            [
                "api-key",
                "selectedModel",
                "selectedLlmModel",
                "streamResponses",
                "selectedVoice",
                "customVoiceId",
                "ttsLowLatency",
                "ttsTextNormalization",
            ],
            keys);
    }

    [Fact]
    public async Task SetSettingValueAsync_PersistsVoiceCustomIdAndTtsToggles()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("selectedVoice", "leo");
        await sut.SetSettingValueAsync("customVoiceId", "custom-voice");
        await sut.SetSettingValueAsync("ttsLowLatency", "true");
        await sut.SetSettingValueAsync("ttsTextNormalization", "true");

        Assert.Equal("leo", await sut.GetSettingValueAsync("selectedVoice"));
        Assert.Equal("custom-voice", await sut.GetSettingValueAsync("customVoiceId"));
        Assert.Equal("custom-voice", sut.SelectedVoiceId);
        Assert.Equal("true", await sut.GetSettingValueAsync("ttsLowLatency"));
        Assert.Equal("true", await sut.GetSettingValueAsync("ttsTextNormalization"));
        Assert.True(sut.TtsLowLatency);
        Assert.True(sut.TtsTextNormalization);
    }

    [Fact]
    public async Task ValidateAsync_FetchesModelsAndVoicesWhenKeyIsValid()
    {
        var handler = new CapturingHandler((request, _) =>
            request.RequestUri?.ToString() switch
            {
                "https://api.x.ai/v1/models" => JsonResponse("""
                    { "data": [ { "id": "grok-4.3", "owned_by": "xai" } ] }
                    """),
                "https://api.x.ai/v1/tts/voices" => JsonResponse("""
                    { "voices": [ { "voice_id": "leo", "name": "Leo" } ] }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "xai-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(["grok-4.3"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["leo"], sut.AvailableVoices.Select(v => v.Id).ToArray());
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Xai", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private abstract record StreamingReceiveItem
    {
        public sealed record Frame(
            byte[] Payload,
            WebSocketMessageType MessageType,
            WebSocketCloseStatus? CloseStatus = null,
            string? CloseDescription = null) : StreamingReceiveItem;
    }

    private sealed record SentStreamingFrame(
        byte[] Payload,
        WebSocketMessageType MessageType);

    private sealed class FakeStreamingWebSocket : WebSocket
    {
        private readonly Channel<StreamingReceiveItem> _receives =
            Channel.CreateUnbounded<StreamingReceiveItem>();
        private readonly List<SentStreamingFrame> _sentFrames = [];
        private readonly Lock _sentLock = new();
        private readonly TaskCompletionSource _receiveExited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;

        public IReadOnlyList<SentStreamingFrame> SentFrames
        {
            get
            {
                lock (_sentLock)
                {
                    return _sentFrames.ToArray();
                }
            }
        }

        public Task ReceiveExited => _receiveExited.Task;
        public bool AbortCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- WebSocket declares these get-only, so an override cannot add a private setter.
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- WebSocket declares these get-only, so an override cannot add a private setter.
        public override string? CloseStatusDescription => _closeDescription;
        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- WebSocket declares these get-only, so an override cannot add a private setter.
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueText(string json) =>
            _receives.Writer.TryWrite(new StreamingReceiveItem.Frame(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text));

        public void EnqueueClose(
            WebSocketCloseStatus closeStatus,
            string? closeDescription) =>
            _receives.Writer.TryWrite(new StreamingReceiveItem.Frame(
                [],
                WebSocketMessageType.Close,
                closeStatus,
                closeDescription));

        public override void Abort()
        {
            AbortCalled = true;
            _state = WebSocketState.Aborted;
            _receives.Writer.TryComplete();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            _receives.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            DisposeCalled = true;
            _state = WebSocketState.Closed;
            _receives.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                var item = await _receives.Reader.ReadAsync(cancellationToken);
                var frame = Assert.IsType<StreamingReceiveItem.Frame>(item);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    _closeStatus = frame.CloseStatus;
                    _closeDescription = frame.CloseDescription;
                    _state = WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(
                        0,
                        WebSocketMessageType.Close,
                        endOfMessage: true,
                        frame.CloseStatus,
                        frame.CloseDescription);
                }

                Assert.True(frame.Payload.Length <= buffer.Count);
                frame.Payload.CopyTo(buffer.Array!, buffer.Offset);
                return new WebSocketReceiveResult(
                    frame.Payload.Length,
                    frame.MessageType,
                    endOfMessage: true);
            }
            finally
            {
                _receiveExited.TrySetResult();
            }
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketState.Open, _state);
            Assert.True(endOfMessage);
            lock (_sentLock)
            {
                _sentFrames.Add(new SentStreamingFrame(
                    buffer.AsSpan().ToArray(),
                    messageType));
            }

            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketState.Open, _state);
            Assert.True(endOfMessage);
            lock (_sentLock)
            {
                _sentFrames.Add(new SentStreamingFrame(
                    buffer.ToArray(),
                    messageType));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(s_jsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public IPluginPcmPlaybackService PcmPlayback { get; init; } =
            UnavailablePluginPcmPlaybackService.Instance;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

}
