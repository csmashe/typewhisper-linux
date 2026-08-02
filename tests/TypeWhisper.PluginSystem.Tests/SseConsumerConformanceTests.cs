using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Claude;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SseConsumerConformanceTests
{
    public enum Consumer
    {
        SharedOpenAiCompatible,
        XaiResponses,
        ClaudeMessages,
        BufferedChatGpt,
    }

    public static TheoryData<Consumer> Consumers => [.. Enum.GetValues<Consumer>()];

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task OptionalDataFieldSpacing_AcceptsBothFormsInOrder(Consumer consumer)
    {
        var wire = DataEvent(DeltaPayload(consumer, "one"), includeSpace: false)
                   + DataEvent(DeltaPayload(consumer, "two"))
                   + TerminalEvent(consumer);

        Assert.Equal("onetwo", await RunConsumerAsync(consumer, wire));
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task Comments_DoNotDispatchAndDoNotSplitMultilineData(Consumer consumer)
    {
        var (first, second) = SplitDeltaPayload(consumer, "joined");
        var wire = ": ping\n\n"
                   + $"data: {first}\n"
                   + ": still working\n"
                   + $"data: {second}\n\n"
                   + TerminalEvent(consumer);

        Assert.Equal("joined", await RunConsumerAsync(consumer, wire));
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task Fragmentation_OneByteReadsIncludingSplitUtf8MatchNormalReads(Consumer consumer)
    {
        var wire = DataEvent(DeltaPayload(consumer, "café")) + TerminalEvent(consumer);

        var unfragmented = await RunConsumerAsync(consumer, wire);
        var fragmented = await RunConsumerAsync(consumer, wire, fragmentResponse: true);

        Assert.Equal("café", unfragmented);
        Assert.Equal(unfragmented, fragmented);
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task MultilineData_FormsOneJsonPayloadAndOneDelta(Consumer consumer)
    {
        var (first, second) = SplitDeltaPayload(consumer, "single");
        var wire = $"data: {first}\ndata: {second}\n\n" + TerminalEvent(consumer);

        Assert.Equal("single", await RunConsumerAsync(consumer, wire));
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task ErrorAfterDelta_ThrowsWithoutSuccessfulCompletion(Consumer consumer)
    {
        var observed = new List<string>();
        var wire = DataEvent(DeltaPayload(consumer, "partial-secret"))
                   + ErrorEvent(consumer)
                   + TerminalEvent(consumer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunConsumerAsync(consumer, wire, observed: observed));

        Assert.IsNotType<IncompleteSseStreamException>(exception);
        if (consumer == Consumer.BufferedChatGpt)
        {
            Assert.DoesNotContain("partial-secret", exception.Message);
            Assert.DoesNotContain("provider-secret", exception.Message);
        }
        else
        {
            Assert.Equal(["partial-secret"], observed);
        }
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task SuccessfulTerminal_CompletesWithExpectedText(Consumer consumer)
    {
        var wire = DataEvent(DeltaPayload(consumer, "complete")) + TerminalEvent(consumer);

        Assert.Equal("complete", await RunConsumerAsync(consumer, wire));
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task PartialEof_ThrowsExactIncompleteStreamType(Consumer consumer)
    {
        var wire = DataEvent(DeltaPayload(consumer, "partial"));

        var exception = await Assert.ThrowsAsync<IncompleteSseStreamException>(
            () => RunConsumerAsync(consumer, wire));

        Assert.NotEmpty(exception.StreamName);
        Assert.NotEmpty(exception.ExpectedTerminal);
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task UnterminatedFinalTerminalEvent_IsDiscardedAndThrowsIncomplete(Consumer consumer)
    {
        var wire = DataEvent(DeltaPayload(consumer, "partial"))
                   + TerminalEvent(consumer).TrimEnd('\n')
                   + "\n";

        await Assert.ThrowsAsync<IncompleteSseStreamException>(
            () => RunConsumerAsync(consumer, wire));
    }

    private static string DeltaPayload(Consumer consumer, string text) => consumer switch
    {
        Consumer.SharedOpenAiCompatible =>
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = text } } },
            }),
        Consumer.XaiResponses or Consumer.BufferedChatGpt =>
            JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text }),
        Consumer.ClaudeMessages =>
            JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                delta = new { type = "text_delta", text },
            }),
        _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
    };

    private static (string First, string Second) SplitDeltaPayload(
        Consumer consumer,
        string text) => consumer switch
    {
        Consumer.SharedOpenAiCompatible =>
            ("{\"choices\":[{\"delta\":", $"{{\"content\":\"{text}\"}}}}]}}"),
        Consumer.XaiResponses or Consumer.BufferedChatGpt =>
            ("{\"type\":\"response.output_text.delta\",", $"\"delta\":\"{text}\"}}"),
        Consumer.ClaudeMessages =>
            ("{\"type\":\"content_block_delta\",\"delta\":",
                $"{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}"),
        _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
    };

    private static string TerminalEvent(Consumer consumer) => consumer switch
    {
        Consumer.SharedOpenAiCompatible or Consumer.BufferedChatGpt => DataEvent("[DONE]"),
        Consumer.XaiResponses => DataEvent("{\"type\":\"response.completed\"}"),
        Consumer.ClaudeMessages =>
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
    };

    private static string ErrorEvent(Consumer consumer) => consumer switch
    {
        Consumer.SharedOpenAiCompatible =>
            DataEvent("{\"error\":{\"message\":\"provider-secret\"}}"),
        Consumer.XaiResponses =>
            DataEvent("{\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"message\":\"provider-secret\"}}}"),
        Consumer.ClaudeMessages => "event: error\ndata: not-json\n\n",
        Consumer.BufferedChatGpt =>
            DataEvent("{\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"message\":\"provider-secret\"}}}"),
        _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
    };

    private static string DataEvent(string payload, bool includeSpace = true) =>
        $"data:{(includeSpace ? " " : "")}{payload}\n\n";

    private static async Task<string?> RunConsumerAsync(
        Consumer consumer,
        string wire,
        bool fragmentResponse = false,
        List<string>? observed = null)
    {
        using var handler = new SseResponseHandler(() => CreateContent(wire, fragmentResponse));
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        switch (consumer)
        {
            case Consumer.SharedOpenAiCompatible:
            {
                var chunks = observed ?? [];
                await foreach (var chunk in OpenAiChatHelper.SendChatCompletionStreamingAsync(
                                   httpClient,
                                   "https://example.test",
                                   "key",
                                   "model",
                                   "system",
                                   "user",
                                   CancellationToken.None))
                {
                    chunks.Add(chunk);
                }

                return string.Concat(chunks);
            }
            case Consumer.XaiResponses:
            {
                var chunks = observed ?? [];
                var client = new XaiResponsesClient(httpClient, "https://example.test", "key");
                await foreach (var chunk in client.ProcessStreamingAsync(
                                   "system", "user", "model", CancellationToken.None))
                {
                    chunks.Add(chunk);
                }

                return string.Concat(chunks);
            }
            case Consumer.ClaudeMessages:
            {
                var chunks = observed ?? [];
                var plugin = new ClaudePlugin(httpClient);
                await plugin.SetApiKeyAsync("key");
                await foreach (var chunk in plugin.ProcessStreamingAsync(
                                   "system", "user", "model", CancellationToken.None))
                {
                    chunks.Add(chunk);
                }

                return string.Concat(chunks);
            }
            case Consumer.BufferedChatGpt:
            {
                var client = new OpenAiChatGptClient(httpClient, "token", null);
                return await client.ProcessAsync(
                    "system", "user", "model", null, CancellationToken.None);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(consumer));
        }
    }

    private static HttpContent CreateContent(string wire, bool fragmented)
    {
        HttpContent content = fragmented
            ? new StreamContent(new OneByteReadStream(Encoding.UTF8.GetBytes(wire)))
            : new StringContent(wire, Encoding.UTF8, "text/event-stream");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream")
        {
            CharSet = "utf-8",
        };
        return content;
    }

    private sealed class SseResponseHandler(Func<HttpContent> contentFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = contentFactory(),
            });
    }

    private sealed class OneByteReadStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytes.Length || count == 0)
                return 0;

            buffer[offset] = bytes[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= bytes.Length || buffer.Length == 0)
                return ValueTask.FromResult(0);

            buffer.Span[0] = bytes[_position++];
            return ValueTask.FromResult(1);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
