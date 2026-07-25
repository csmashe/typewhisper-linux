using System.Net;
using System.Reflection;
using System.Text;
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
    public void ParseChatCompletionStreamDelta_GarbageFrame_ReturnsNull()
    {
        Assert.Null(OpenAiChatHelper.ParseChatCompletionStreamDelta("not json"));
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
}
