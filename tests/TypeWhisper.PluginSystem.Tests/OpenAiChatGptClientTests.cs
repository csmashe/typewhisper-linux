using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiChatGptClientTests
{
    [Fact]
    public async Task ParseResponseText_SseDeltaThenEof_Throws()
    {
        var stream = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial-secret\"}",
            "",
            "");

        var ex = await Assert.ThrowsAsync<IncompleteSseStreamException>(() =>
            OpenAiChatGptClient.ParseResponseTextAsync(stream));

        Assert.Equal("ChatGPT SSE stream", ex.StreamName);
        Assert.Equal("[DONE]", ex.ExpectedTerminal);
        Assert.DoesNotContain("partial-secret", ex.Message);
    }

    [Theory]
    [InlineData(
        """{"type":"error","error":{"message":"provider-secret"}}""",
        "error",
        null)]
    [InlineData(
        """{"type":"response.failed","response":{"status":"failed","error":{"message":"provider-secret"}}}""",
        "response.failed",
        "failed")]
    [InlineData(
        """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"provider-secret"}}}""",
        "response.incomplete",
        "incomplete")]
    [InlineData(
        """{"type":"response.cancelled","response":{"status":"cancelled"}}""",
        "response.cancelled",
        "cancelled")]
    [InlineData(
        """{"type":"response.canceled","response":{"status":"canceled"}}""",
        "response.canceled",
        "canceled")]
    public async Task ParseResponseText_SseFailureEventAfterDelta_Throws(
        string failurePayload,
        string eventType,
        string? status)
    {
        var stream = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial-secret\"}",
            "",
            $"data: {failurePayload}",
            "",
            "data: [DONE]",
            "",
            "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiChatGptClient.ParseResponseTextAsync(stream));

        Assert.Contains(eventType, ex.Message);
        if (status is not null)
            Assert.Contains(status, ex.Message);
        Assert.DoesNotContain("partial-secret", ex.Message);
        Assert.DoesNotContain("provider-secret", ex.Message);
    }

    [Fact]
    public async Task ParseResponseText_SseDoneTerminatedStream_ReturnsText()
    {
        var stream = string.Join(
            "\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}",
            "",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\" world\"}",
            "",
            "data: [DONE]",
            "",
            "");

        var result = await OpenAiChatGptClient.ParseResponseTextAsync(stream);

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public async Task ParseResponseText_SseResponseCompletedWithNonCompletedNestedStatus_Throws()
    {
        const string stream = """
                              data: {"type":"response.output_text.delta","delta":"partial-secret"}

                              data: {"type":"response.completed","response":{"status":"incomplete","output_text":"response-secret"}}

                              data: [DONE]

                              """;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiChatGptClient.ParseResponseTextAsync(stream));

        Assert.Contains("response.completed", ex.Message);
        Assert.Contains("incomplete", ex.Message);
        Assert.DoesNotContain("partial-secret", ex.Message);
        Assert.DoesNotContain("response-secret", ex.Message);
    }
}
