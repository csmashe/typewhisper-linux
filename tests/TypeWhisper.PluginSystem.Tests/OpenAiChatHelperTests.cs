using System.Reflection;
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
            typeof(CancellationToken)
        };

        var method = typeof(OpenAiChatHelper).GetMethod(
            nameof(OpenAiChatHelper.SendChatCompletionAsync),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null
        );

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method!.ReturnType);
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
}
