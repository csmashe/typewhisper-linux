using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Claude;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

// C7 Phase 5: Claude is bespoke Anthropic Messages SSE (content_block_delta →
// delta.text, no [DONE] sentinel, error frames after a 200). These exercise the
// self-gated ProcessStreamingAsync + the reflection-free frame parsers.
public sealed class ClaudePluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestFailure_LogsStatusAndReasonWithoutBody(bool streaming)
    {
        const string body = "private transcript and remembered context";
        using var client = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request", Content = new StringContent(body),
            }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "key" } };
        using var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(host);
        var error = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in sut.ProcessStreamingAsync("system", "user", "claude", CancellationToken.None)) { }
            }
            else
                await sut.ProcessAsync("system", "user", "claude", CancellationToken.None);
        });
        Assert.Equal("Anthropic API returned 400: Bad Request", error.Message);
        Assert.DoesNotContain(body, error.Message);
        Assert.Contains("Anthropic API error 400: Bad Request", host.Messages);
        Assert.All(host.Messages, message => Assert.DoesNotContain(body, message));
    }

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<ClaudePlugin>();


    [Fact]
    public async Task ProcessAsync_ScalesMaxTokensAndRejectsMaxTokensStopReason()
    {
        var input = string.Concat(Enumerable.Repeat("dictated input ", 1000));
        using var client = new HttpClient(new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal(LlmOutputTokenBudget.CalculateWithReasoningReserve("system", input), doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() > 2048);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"stop_reason":"max_tokens","content":[{"type":"text","text":"partial"}]}""") };
        }));
        var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "key" } });
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync("system", input, "claude", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
    }

    [Fact]
    public async Task ProcessStreamingAsync_MaxTokensStopReason_ThrowsTruncation()
    {
        const string sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        using var client = new HttpClient(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") }));
        var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "key" } });
        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "claude", CancellationToken.None))
                chunks.Add(chunk);
        });
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
        Assert.Equal(["partial"], chunks);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsContentBlockDeltasInOrder()
    {
        string? capturedBody = null;
        var sse = string.Join(
            "\n",
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\"}}",
            "",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}",
            "",
            "event: content_block_stop",
            "data: {\"type\":\"content_block_stop\",\"index\":0}",
            "",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}",
            "",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedBody = body;
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync(
            "system", "user", "claude-haiku-4-5-20251001", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["Hel", "lo"], chunks);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("claude-haiku-4-5-20251001", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"stop_reason":"end_turn","content":[{"type":"text","text":"bulk"}]}""",
                Encoding.UTF8, "application/json"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync(
            "system", "user", "model", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsOnErrorFrameAfterPartialDeltas()
    {
        // A Messages stream returns 200 then can fail mid-flight via an
        // `event: error` frame. The reader must throw so LlmStreamPump faults and
        // the caller falls back to batch, rather than committing the partial.
        var sse = string.Join(
            "\n",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}",
            "",
            "event: error",
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "model", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["Hel"], chunks);
        Assert.Equal("Overloaded", ex.Message);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsWhenEofPrecedesMessageStop()
    {
        var sse = string.Join(
            "\n",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<IncompleteSseStreamException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "model", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["partial"], chunks);
        Assert.Equal("Anthropic stream", ex.StreamName);
        Assert.Equal("a message_stop event", ex.ExpectedTerminal);
    }

    [Theory]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""", "hi")]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}""", null)]
    [InlineData("""{"type":"message_start","message":{"id":"msg"}}""", null)]
    [InlineData("""{"type":"message_stop"}""", null)]
    [InlineData("not json", null)]
    public void ParseStreamDelta_ExtractsOnlyTextDeltaFrames(string payload, string? expected)
    {
        Assert.Equal(expected, ClaudePlugin.ParseStreamDelta(payload));
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"boom"}}""", "boom")]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error"}}""", "Anthropic streaming error.")]
    [InlineData("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}""", null)]
    [InlineData("""{"type":"message_stop"}""", null)]
    [InlineData("not json", null)]
    public void ParseStreamError_DetectsErrorFrames(string payload, string? expected)
    {
        Assert.Equal(expected, ClaudePlugin.ParseStreamError(payload));
    }

    private const string ValidKey = "sk-ant-test";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<ClaudePlugin> ActivatedAsync(
        HttpMessageHandler handler, TestPluginHostServices? host = null)
    {
        host ??= new TestPluginHostServices();
        host.Secrets["api-key"] = ValidKey;
        var sut = new ClaudePlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    private static string[] Ids(ClaudePlugin plugin) => plugin.SupportedModels.Select(m => m.Id).ToArray();

    private static readonly string[] s_fallbackIds =
        ["claude-sonnet-5", "claude-opus-5", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];

    [Fact]
    public void FallbackCatalog_ReplacesRetiredSonnet4Default()
    {
        using var sut = new ClaudePlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));

        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(ClaudePlugin.DefaultModelId, sut.SupportedModels[0].Id);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "claude-sonnet-4-20250514");
    }

    [Theory]
    [InlineData("", "claude-sonnet-5")]
    [InlineData("  ", "claude-sonnet-5")]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4-20250514")]
    [InlineData(" account-custom-model ", "account-custom-model")]
    public async Task ProcessAsync_SendsExplicitModelUnchangedAndDefaultsBlankModel(string model, string expected)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal(expected, doc.RootElement.GetProperty("model").GetString());
            return Json("""{"stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}""");
        }));

        Assert.Equal("ok", await sut.ProcessAsync("system", "user", model, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessAsync_RetiredModelExplainsRecoveryWithoutSwappingModels()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal("claude-sonnet-4-20250514", doc.RootElement.GetProperty("model").GetString());
            return Json("""{"type":"error","error":{"type":"not_found_error","message":"model: claude-sonnet-4-20250514"}}""",
                HttpStatusCode.NotFound);
        }), host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ProcessAsync("system", "user", "claude-sonnet-4-20250514", CancellationToken.None));

        Assert.Equal("Errors.ModelNotFound", error.Message);
        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind);
        Assert.Equal(404, error.HttpStatusCode);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task ProcessAsync_SkipsThinkingBlocksAndJoinsTextBlocks()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"private"},{"type":"redacted_thinking","data":"x"},{"type":"text","text":" Hello "},{"type":"text","text":"team. "}]}""")));

        Assert.Equal("Hello team.", await sut.ProcessAsync("system", "user", "", CancellationToken.None));
    }

    [Theory]
    [InlineData("", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("not json", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("[]", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{}", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"content":[{"type":"text","text":"no stop reason"}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"end_turn","content":[42]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"end_turn","content":{}}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"end_turn","content":[]}""", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"hidden"}]}""", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"stop_reason":"end_turn","content":[{"type":"text","text":"   "}]}""", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"stop_reason":"end_turn","content":[{"type":"text","text":null}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"end_turn","content":[{"type":"tool_use","id":"t","name":"n","input":{}}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"tool_use","content":[{"type":"text","text":"partial"}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"pause_turn","content":[{"type":"text","text":"partial"}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"refusal","content":[{"type":"text","text":"partial"}]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"stop_reason":"max_tokens","content":[]}""", PluginRequestFailureKind.OutputTruncated)]
    [InlineData("""{"stop_reason":"model_context_window_exceeded","content":[]}""", PluginRequestFailureKind.OutputTruncated)]
    public void ParseMessage_RejectsInvalidOrUnfinishedAnswers(string body, PluginRequestFailureKind kind)
    {
        var error = Assert.Throws<PluginRequestException>(() => ClaudePlugin.ParseMessage(body));
        Assert.Equal(kind, error.FailureKind);
    }

    [Theory]
    [InlineData("refusal", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("tool_use", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("pause_turn", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("model_context_window_exceeded", PluginRequestFailureKind.OutputTruncated)]
    public async Task ProcessStreamingAsync_RejectsUnfinishedStopReasons(string stopReason, PluginRequestFailureKind kind)
    {
        var sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"
            + $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stopReason}\"}}}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") }));

        var error = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var _ in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None)) { }
        });
        Assert.Equal(kind, error.FailureKind);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThinkingOnlyStreamIsEmptyResponse()
    {
        const string sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"private\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") }));

        var chunks = new List<string>();
        var error = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
                chunks.Add(chunk);
        });
        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ValidateAsync_FollowsCursorsAndCommitsTheCompleteCatalog()
    {
        var host = new TestPluginHostServices();
        var calls = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal(ValidKey, request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            if (++calls == 1)
            {
                Assert.Equal("?limit=1000", request.RequestUri.Query);
                return Json("""{"data":[{"id":"claude-newest","display_name":"Claude Newest"}],"has_more":true,"last_id":"cursor/one"}""");
            }

            Assert.Equal("?limit=1000&after_id=cursor%2Fone", request.RequestUri.Query);
            return Json("""{"data":[{"id":"claude-sonnet-5","display_name":"Claude Sonnet 5"},{"id":"claude-older"},{"id":"claude-newest"}],"has_more":false}""");
        }), host);
        var capabilityChanges = host.CapabilityChanges;

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidFetchedModels", result.Message);
        Assert.Equal(2, calls);
        // The upstream default leads when the account offers it; the rest keep API order.
        Assert.Equal(["claude-sonnet-5", "claude-newest", "claude-older"], Ids(sut));
        Assert.Equal(["Claude Sonnet 5", "Claude Newest", "claude-older"], sut.SupportedModels.Select(m => m.DisplayName));
        Assert.Equal(capabilityChanges + 1, host.CapabilityChanges);

        using var restarted = new ClaudePlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await restarted.ActivateAsync(host);
        Assert.Equal(Ids(sut), Ids(restarted));
    }

    [Fact]
    public async Task ValidateAsync_FailedCatalogSaveLeavesLiveCatalogUnchanged()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"claude-unsaved"}],"has_more":false}""")), host);
        var capabilityChanges = host.CapabilityChanges;
        host.FailSettingWrites = true;

        await Assert.ThrowsAsync<IOException>(() => sut.ValidateAsync());

        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(capabilityChanges, host.CapabilityChanges);
    }

    [Fact]
    public async Task ValidateAsync_CatalogWithoutDefaultKeepsAccountOrder()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"claude-b"},{"id":"claude-a"}],"has_more":false}""")));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["claude-b", "claude-a"], Ids(sut));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"data":[],"has_more":false}""")]
    [InlineData("""{"data":[{"id":"claude-a"}]}""")]
    [InlineData("""{"data":[{"id":"claude-a"}],"has_more":"false"}""")]
    [InlineData("""{"data":{},"has_more":false}""")]
    [InlineData("""{"data":[{"id":42}],"has_more":false}""")]
    [InlineData("""{"data":[{"id":"bad id"}],"has_more":false}""")]
    [InlineData("""{"data":[{"id":"claude-a"}],"has_more":true}""")]
    [InlineData("""{"data":[],"has_more":true,"last_id":"claude-a"}""")]
    [InlineData("""{"data":[{"id":"claude-a"}],"has_more":true,"last_id":"bad cursor"}""")]
    public async Task ValidateAsync_MalformedCatalogKeepsSavedModels(string body)
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(body)), host);
        var writes = host.SettingWrites;

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_RepeatedOrUnboundedCursorsAreRejected(bool uniqueCursors)
    {
        var host = new TestPluginHostServices();
        var calls = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
        {
            var cursor = uniqueCursors ? "cursor-" + ++calls : "repeated";
            return Json($$"""{"data":[{"id":"claude-{{cursor}}"}],"has_more":true,"last_id":"{{cursor}}"}""");
        }), host);
        var writes = host.SettingWrites;

        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(s_fallbackIds, Ids(sut));
        if (uniqueCursors)
            Assert.Equal(20, calls);
    }

    [Fact]
    public async Task ValidateAsync_FailedLaterPageDoesNotCommitPartialCatalog()
    {
        var calls = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => ++calls == 1
            ? Json("""{"data":[{"id":"claude-partial"}],"has_more":true,"last_id":"claude-partial"}""")
            : Json("""{"error":{"message":"unavailable"}}""", HttpStatusCode.ServiceUnavailable)));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "claude-partial");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ValidateAsync_RejectedKeyIsReported(HttpStatusCode status)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"type":"error","error":{"type":"authentication_error"}}""", status)));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyRejected", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_BadKeyFormatStaysOffline()
    {
        var host = new TestPluginHostServices();
        using var sut = new ClaudePlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        host.Secrets["api-key"] = "not-an-anthropic-key";
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyFormatInvalid", result.Message);
    }

    [Fact]
    public async Task ApiKeyChange_ForgetsTheOtherAccountsCatalog()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"claude-account-a"}],"has_more":false}""")), host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["claude-account-a"], Ids(sut));

        await sut.SetSettingValueAsync("api-key", ValidKey);
        Assert.Equal(["claude-account-a"], Ids(sut));

        await sut.SetSettingValueAsync("api-key", "sk-ant-other");
        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Empty(host.GetSetting<List<JsonElement>>("fetchedModels")!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_RefreshOutlivedByKeyChangeOrDeactivationIsDiscarded(bool deactivate)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new AsyncHandler(async _ =>
        {
            entered.TrySetResult();
            await finish.Task;
            return Json("""{"data":[{"id":"claude-stale"}],"has_more":false}""");
        }), host);

        var pending = sut.ValidateAsync();
        await entered.Task;
        if (deactivate)
            await sut.DeactivateAsync();
        else
            await sut.SetSettingValueAsync("api-key", "sk-ant-replacement");
        finish.SetResult();

        var result = await pending;
        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshStale", result.Message);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "claude-stale");
        Assert.DoesNotContain(host.GetSetting<List<JsonElement>>("fetchedModels") ?? [],
            m => m.GetProperty("Id").GetString() == "claude-stale");
    }

    [Fact]
    public async Task ValidateAsync_DiscoveryIsBoundedByDeadline()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = ValidKey } };
        var plugin = new ClaudePlugin(new HttpClient(new AsyncHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }))) { ModelDiscoveryDeadline = TimeSpan.FromMilliseconds(50) };
        using var sut = plugin;
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        using var sut = await ActivatedAsync(new AsyncHandler(async ct =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the awaited ValidateAsync below.
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ValidateAsync(cts.Token));
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task Activate_DropsInvalidPersistedModels()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fetchedModels", new[]
        {
            new { Id = "claude-kept", DisplayName = (string?)"Kept\a" },
            new { Id = "bad id", DisplayName = (string?)null },
            new { Id = "claude-kept", DisplayName = (string?)"Duplicate" },
        });
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")), host);

        var model = Assert.Single(sut.SupportedModels);
        Assert.Equal("claude-kept", model.Id);
        Assert.Equal("claude-kept", model.DisplayName);
    }

    private sealed class AsyncHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(cancellationToken);
    }

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

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];

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
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value)
        {
            if (FailSettingWrites)
                throw new IOException("Settings store unavailable.");
            SettingWrites++;
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);
        }

        public int SettingWrites { get; private set; }
        public bool FailSettingWrites { get; set; }
        public int CapabilityChanges { get; private set; }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public List<string> Messages { get; } = [];
        public void Log(PluginLogLevel level, string message) => Messages.Add(message);
        public void NotifyCapabilitiesChanged() => CapabilityChanges++;
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
