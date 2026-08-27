using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiChatHelperTests
{
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
