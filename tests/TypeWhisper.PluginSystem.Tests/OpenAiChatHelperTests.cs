using TypeWhisper.PluginSDK;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiChatHelperTests
{
    [Fact]
    public async Task SendChatCompletionAsync_ScalesOutputBudgetForLongInput()
    {
        var input = string.Concat(Enumerable.Repeat("dictated input ", 1000));
        using var doc = JsonDocument.Parse(await CaptureRequestAsync(new OpenAiChatRequestOptions(), input));
        Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() > 2048);
    }

    [Fact]
    public async Task SendChatCompletionAsync_KeepsFloorForShortInput()
    {
        using var doc = JsonDocument.Parse(await CaptureRequestAsync(new OpenAiChatRequestOptions()));
        Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task SendChatCompletionAsync_UsesReasoningReserveWhenReasoningEffortSet()
    {
        using var doc = JsonDocument.Parse(await CaptureRequestAsync(new OpenAiChatRequestOptions { ReasoningEffort = "high" }));
        Assert.Equal(LlmOutputTokenBudget.CalculateWithReasoningReserve("system", "user"),
            doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("MAX_TOKENS")]
    [InlineData("max_output_tokens")]
    [InlineData("model_context_window_exceeded")]
    public async Task SendChatCompletionAsync_RejectsTokenLimitedPartialResponse(string reason)
    {
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => SendChatResponseAsync(
            $$"""{"choices":[{"message":{"content":"partial"},"finish_reason":"{{reason}}"}]}"""));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
        Assert.False(ex.IsTransient);
        Assert.Contains("token", ex.Message);
    }

    [Fact]
    public async Task SendChatCompletionStreamingAsync_RejectsTokenLimitedStream()
    {
        var chunks = new List<string>();
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => StreamChatResponseAsync(sse, chunks));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
        Assert.False(ex.IsTransient);
        Assert.Equal(["partial"], chunks);
    }

    [Theory]
    [InlineData(100, 2048)]
    [InlineData(8000, 4096)]
    [InlineData(100000, 4096)]
    public void OutputTokenBudget_ScalesAndRemainsBounded(int characters, int expected) =>
        Assert.Equal(expected, LlmOutputTokenBudget.Calculate("", new string('x', characters)));

    [Fact]
    public void OutputTokenBudget_AddsReasoningCapacityWithoutReducingVisibleBudget()
    {
        var input = new string('x', 100000);
        Assert.Equal(LlmOutputTokenBudget.Calculate("", input) + LlmOutputTokenBudget.ReasoningReserveTokens,
            LlmOutputTokenBudget.CalculateWithReasoningReserve("", input));
    }

    [Fact]
    public void OutputTokenBudget_CapsLocalGenerationToRemainingContext() =>
        Assert.Equal(596, LlmOutputTokenBudget.FitToContext(2048, 3500, 4096, "Gemma"));

    [Fact]
    public void OutputTokenBudget_RejectsPromptWithoutOutputCapacity()
    {
        var ex = Assert.Throws<PluginRequestException>(() => LlmOutputTokenBudget.FitToContext(2048, 4096, 4096, "Gemma"));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, ex.FailureKind);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task SendChatCompletionAsync_WritesNonAsciiContentLiterally()
    {
        var body = await CaptureRequestAsync(new OpenAiChatRequestOptions(), "今天天气很好,我们去蹓狗吧!");
        Assert.Contains("今天天气很好,我们去蹓狗吧!", body);
        Assert.DoesNotContain(@"\u4eca", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendChatCompletionAsync_AdditionalBodyFields_AreSerialized()
    {
        var body = await CaptureRequestAsync(new OpenAiChatRequestOptions
        {
            AdditionalBodyFields = new Dictionary<string, object?> { ["thinking"] = new { type = "disabled" } },
        });
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("model", root.GetProperty("model").GetString());
        Assert.Equal(2, root.GetProperty("messages").GetArrayLength());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("model")]
    [InlineData("messages")]
    [InlineData("stream")]
    public async Task SendChatCompletionAsync_AdditionalBodyFields_CannotOverrideReservedKeys(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CaptureRequestAsync(new OpenAiChatRequestOptions
        {
            AdditionalBodyFields = new Dictionary<string, object?> { [key] = "override" },
        }));
    }

    [Fact]
    public async Task SendChatCompletionAsync_StripsThinkBlockFromContent()
    {
        Assert.Equal("Final answer.", await SendChatResponseAsync(
            """{"choices":[{"message":{"content":"<think>\nplan\n</think>\n\nFinal answer."}}]}"""));
    }

    [Fact]
    public async Task SendChatCompletionAsync_ThrowsWhenContentIsOnlyThinkBlock()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SendChatResponseAsync(
            """{"choices":[{"message":{"content":"<think>plan</think>\n "}}]}"""));
        Assert.Equal("Chat completion returned only reasoning content and no final answer.", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_IgnoresReasoningContentField()
    {
        Assert.Equal("processed", await SendChatResponseAsync(
            """{"choices":[{"message":{"reasoning_content":"plan","content":"  processed  "}}]}"""));
    }

    [Fact]
    public async Task SendChatCompletionStreamingAsync_DropsThinkBlockSplitAcrossDeltas()
    {
        string[] deltas = ["<thi", "nk>reason", "ing</th", "ink>", "Hel", "lo"];
        var sse = string.Concat(deltas.Select(content => "data: " + JsonSerializer.Serialize(
            new { choices = new[] { new { delta = new { content } } } }) + "\n\n")) + "data: [DONE]\n\n";
        var chunks = new List<string>();
        await StreamChatResponseAsync(sse, chunks);
        Assert.Equal("Hello", string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.DoesNotContain("<think>", chunk, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThinkingBlockStreamFilter_FlushReturnsHeldPartialPrefixThatWasNotATag()
    {
        var filter = new ThinkingBlockStreamFilter();
        Assert.Equal("a <t", string.Concat(filter.Push("a <t")) + filter.Flush());
    }

    [Fact]
    public void ThinkingBlockFilter_Strip_ReturnsSameInstanceWithoutTags()
    {
        var text = new string("  unchanged  ".ToCharArray());
        Assert.Same(text, ThinkingBlockFilter.Strip(text));
    }

    [Fact]
    public void ThinkingBlockFilter_Strip_RemovesUnterminatedBlock()
    {
        Assert.Equal("Answer", ThinkingBlockFilter.Strip("Answer <think>dangling"));
    }

    [Theory]
    [InlineData("<THINK>first\nthought</ThInK>\n\nHello<think>second</think>!", "Hello!")]
    [InlineData("  <think>hidden</think>  Answer  ", "Answer  ")]
    public void ThinkingBlockFilter_Strip_RemovesAllBlocks(string text, string expected)
    {
        Assert.Equal(expected, ThinkingBlockFilter.Strip(text));
    }

    [Theory]
    [InlineData("<THINK>first\nthought</ThInK>\n\nHello<think>second</think>!", "Hello!")]
    [InlineData("Hello <tiger>!", "Hello <tiger>!")]
    [InlineData("<think>hidden", "")]
    [InlineData("a </th", "a </th")]
    public void ThinkingBlockStreamFilter_HandlesEverySplit(string text, string expected)
    {
        for (var split = 0; split <= text.Length; split++)
        {
            var filter = new ThinkingBlockStreamFilter();
            var actual = string.Concat(filter.Push(text[..split]))
                + string.Concat(filter.Push(text[split..])) + filter.Flush();
            Assert.Equal(expected, actual);
        }
        var characterFilter = new ThinkingBlockStreamFilter();
        Assert.Equal(expected, string.Concat(text.SelectMany(c => characterFilter.Push(c.ToString())))
            + characterFilter.Flush());
    }

    [Fact]
    public async Task SendChatCompletionStreamingAsync_WritesNonAsciiContentLiterally()
    {
        var handler = new RequestCaptureHandler("data: [DONE]\n\n");
        using var client = new HttpClient(handler);
        await foreach (var unused in OpenAiChatHelper.SendChatCompletionStreamingAsync(
            client, "https://example.test", "key", "model", "system", "今天天气很好,我们去蹓狗吧!",
            new OpenAiChatRequestOptions(), CancellationToken.None))
        {
            Assert.Fail($"Unexpected delta: {unused}");
        }
        Assert.Contains("今天天气很好,我们去蹓狗吧!", handler.Body);
        Assert.DoesNotContain(@"\u4eca", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendChatCompletionStreamingAsync_ThrowsWhenStreamIsOnlyThinkBlock()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"\\n\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>plan</think>\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"\\n\"}}]}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new RequestCaptureHandler(sse);
        using var client = new HttpClient(handler);
        var chunks = new List<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in OpenAiChatHelper.SendChatCompletionStreamingAsync(
                client, "https://example.test", "key", "model", "system", "user",
                new OpenAiChatRequestOptions(), CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Empty(chunks);
        Assert.Contains("reasoning", error.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_ScaleOutputTokensFalse_SendsFloorUnchanged()
    {
        var longInput = string.Concat(Enumerable.Repeat("dictated input ", 1_000));
        var body = await CaptureRequestAsync(new OpenAiChatRequestOptions { ScaleOutputTokens = false }, longInput);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private static async Task<string> CaptureRequestAsync(OpenAiChatRequestOptions options, string user = "user")
    {
        var handler = new RequestCaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var client = new HttpClient(handler);
        await OpenAiChatHelper.SendChatCompletionAsync(
            client, "https://example.test", "key", "model", "system", user, options, CancellationToken.None);
        return handler.Body!;
    }

    private sealed class RequestCaptureHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    [Fact]
    public void SendChatCompletionAsync_PreservesLegacySevenParameterOverload()
    {
        var parameterTypes = new[]
        {
            typeof(HttpClient),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(CancellationToken),
        };

        var method = typeof(OpenAiChatHelper).GetMethod(
            nameof(OpenAiChatHelper.SendChatCompletionAsync),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null
        );

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method.ReturnType);
    }

    [Fact]
    public async Task SendChatCompletionAsync_MissingChoices_ThrowsButExplicitEmptyContentSucceeds()
    {
        var emptyResult = await SendChatResponseAsync(
            """{"choices":[{"message":{"content":""}}]}""");
        const string json = """{"id":"chatcmpl-123"}""";

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Equal("", emptyResult);
        Assert.Contains("'choices'", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_EmptyChoices_ThrowsProtocolFailure()
    {
        const string json = """{"choices":[]}""";

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Contains("'choices'", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_ChoiceWithoutMessage_ThrowsProtocolFailure()
    {
        const string json = """{"choices":[{"finish_reason":"stop"}]}""";

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Contains("'choices[0].message'", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_MessageWithoutContent_ThrowsProtocolFailure()
    {
        const string json = """{"choices":[{"message":{"role":"assistant"}}]}""";

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Contains("'choices[0].message.content'", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_NonStringContent_ThrowsProtocolFailure()
    {
        const string json = """{"choices":[{"message":{"content":42}}]}""";

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Contains("'choices[0].message.content'", exception.Message);
    }

    [Fact]
    public async Task SendChatCompletionAsync_SuccessfulErrorObject_SurfacesProviderMessage()
    {
        const string json = """
                            {
                                "error": {
                                    "message": "The provider rejected this request.",
                                    "type": "invalid_request_error"
                                }
                            }
                            """;

        var exception = await AssertProtocolFailureAsync(json);

        Assert.Contains("'choices'", exception.Message);
        Assert.Contains("The provider rejected this request.", exception.Message);
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_ExtractsContentDelta()
    {
        Assert.Equal(
            "Hello",
            OpenAiChatHelper.ParseChatCompletionStreamDelta(
                """{"choices":[{"delta":{"content":"Hello"}}]}"""));
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_RoleOnlyFrame_ReturnsNull()
    {
        Assert.Null(OpenAiChatHelper.ParseChatCompletionStreamDelta(
            """{"choices":[{"delta":{"role":"assistant"}}]}"""));
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_FinishFrame_ReturnsNull()
    {
        Assert.Null(OpenAiChatHelper.ParseChatCompletionStreamDelta(
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}"""));
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_ExplicitNullContent_ReturnsNull()
    {
        Assert.Null(OpenAiChatHelper.ParseChatCompletionStreamDelta(
            """{"choices":[{"delta":{"content":null}}]}"""));
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_ExplicitEmptyContent_ReturnsEmpty()
    {
        Assert.Equal("", OpenAiChatHelper.ParseChatCompletionStreamDelta(
            """{"choices":[{"delta":{"content":""}}]}"""));
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_GarbageFrame_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() =>
            OpenAiChatHelper.ParseChatCompletionStreamDelta("not json"));
    }

    [Theory]
    [InlineData("""{"id":"chatcmpl-123"}""")]
    [InlineData("""{"choices":{}}""")]
    [InlineData("""{"choices":[]}""")]
    public void ParseChatCompletionStreamDelta_InvalidChoices_ThrowsProtocolFailure(string json)
    {
        var exception = AssertStreamProtocolFailure(json);

        Assert.Contains("'choices'", exception.Message);
    }

    [Theory]
    [InlineData("""{"choices":[{}]}""")]
    [InlineData("""{"choices":[{"delta":[]}] }""")]
    public void ParseChatCompletionStreamDelta_InvalidDelta_ThrowsProtocolFailure(string json)
    {
        var exception = AssertStreamProtocolFailure(json);

        Assert.Contains("'choices[0].delta'", exception.Message);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void ParseChatCompletionStreamDelta_NonStringContent_ThrowsProtocolFailure(
        string contentJson)
    {
        var json = $"{{\"choices\":[{{\"delta\":{{\"content\":{contentJson}}}}}]}}";

        var exception = AssertStreamProtocolFailure(json);

        Assert.Contains("'choices[0].delta.content'", exception.Message);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"\"")]
    public async Task SendChatCompletionStreamingAsync_InvalidFinishReasonThenDone_Completes(
        string finishReasonJson)
    {
        var sse = string.Join(
            "\n",
            $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"Hello\"}},\"finish_reason\":{finishReasonJson}}}]}}",
            "",
            "data: [DONE]",
            "",
            "");
        var chunks = new List<string>();

        await StreamChatResponseAsync(sse, chunks);

        Assert.Equal(["Hello"], chunks);
    }

    [Fact]
    public async Task SendChatCompletionStreamingAsync_MalformedDeltaBetweenTextAndDone_Throws()
    {
        var sse = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":42}}]}",
            "",
            "data: [DONE]",
            "",
            "");
        var chunks = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamChatResponseAsync(sse, chunks));

        Assert.Equal(["Hello"], chunks);
        Assert.Contains("'choices[0].delta.content'", exception.Message);
    }

    [Fact]
    public void ParseChatCompletionStreamDelta_LongInvalidPayload_TruncatesBodySnippet()
    {
        var json = $$"""{"padding":"{{new string('x', 240)}}not-in-snippet"}""";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenAiChatHelper.ParseChatCompletionStreamDelta(json));
        var body = exception.Message[(exception.Message.IndexOf("Body: ", StringComparison.Ordinal)
                                      + "Body: ".Length)..];

        Assert.Equal($"{json[..200]}...", body);
        Assert.DoesNotContain("not-in-snippet", exception.Message);
    }

    [Theory]
    [InlineData("""{"error":{"message":"server had an error","type":"server_error"}}""", "server had an error")]
    [InlineData("""{"error":{"type":"server_error"}}""", "Streaming error.")]
    [InlineData("""{"error":"flat string error"}""", "flat string error")]
    public void ParseChatCompletionStreamError_DetectsErrorFrames(string payload, string expected)
    {
        Assert.Equal(expected, OpenAiChatHelper.ParseChatCompletionStreamError(payload));
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"Hello"}}]}""")]
    [InlineData("""{"choices":[{"delta":{"content":"Hi"}}],"error":null}""")] // literal error:null is not a failure
    [InlineData("not json")]
    public void ParseChatCompletionStreamError_NonErrorFrame_ReturnsNull(string payload)
    {
        Assert.Null(OpenAiChatHelper.ParseChatCompletionStreamError(payload));
    }

    private static async Task<InvalidOperationException> AssertProtocolFailureAsync(string json)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendChatResponseAsync(json));
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json.Length > 200 ? json[..200] : json, exception.Message);
        return exception;
    }

    private static InvalidOperationException AssertStreamProtocolFailure(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenAiChatHelper.ParseChatCompletionStreamDelta(json));
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json.Length > 200 ? json[..200] : json, exception.Message);
        return exception;
    }

    private static async Task<string> SendChatResponseAsync(string json)
    {
        using var httpClient = new HttpClient(new JsonResponseHandler(json));
        return await OpenAiChatHelper.SendChatCompletionAsync(
            httpClient,
            "https://example.test",
            "test-key",
            "test-model",
            "system",
            "user",
            CancellationToken.None
        );
    }

    private static async Task StreamChatResponseAsync(string sse, List<string> chunks)
    {
        using var httpClient = new HttpClient(new SseResponseHandler(sse));
        await foreach (var chunk in OpenAiChatHelper.SendChatCompletionStreamingAsync(
                           httpClient,
                           "https://example.test",
                           "test-key",
                           "test-model",
                           "system",
                           "user",
                           CancellationToken.None
                       ))
        {
            chunks.Add(chunk);
        }
    }

    private sealed class JsonResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SseResponseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
