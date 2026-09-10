using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using System.Text;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LlmRequestRetryPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingBodyReset_RetriesOnlyBeforeFirstToken(bool afterToken)
    {
        var attempts = 0;
        const string delta = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
        using var client = new HttpClient(new ResponseHandler(() =>
        {
            attempts++;
            Stream body = attempts == 1
                ? new ResetStream(afterToken ? delta : "")
                : new MemoryStream(Encoding.UTF8.GetBytes(delta + "data: [DONE]\n\n"));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
        }));
        var tokens = new List<string>();

        if (afterToken)
        {
            var error = await Assert.ThrowsAsync<PluginRequestException>(Drain);
            Assert.Equal(PluginRequestFailureKind.Network, error.FailureKind);
            Assert.IsType<IOException>(error.InnerException);
        }
        else
            await Drain();

        Assert.Equal(afterToken ? 1 : 2, attempts);
        Assert.Equal(["hello"], tokens);
        return;

        async Task Drain()
        {
            await foreach (var token in LlmRequestRetryPolicy.ExecuteStreamingAsync(
                               // ReSharper disable once AccessToDisposedClosure -- Drain is awaited above, before the using disposes client at scope end.
                               ct => OpenAiChatHelper.SendChatCompletionStreamingAsync(
                                   client, "https://example.test", "key", "model", "system", "user", ct),
                               CancellationToken.None))
                tokens.Add(token);
        }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private sealed class ResetStream(string prefix) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length
                ? ValueTask.FromException<int>(new IOException("connection reset"))
                : base.ReadAsync(buffer, cancellationToken);
    }

    [Theory]
    [InlineData(PluginRequestFailureKind.Network)]
    [InlineData(PluginRequestFailureKind.Timeout)]
    [InlineData(PluginRequestFailureKind.RateLimit)]
    [InlineData(PluginRequestFailureKind.ServerError)]
    public async Task TransientFailure_RetriesTwiceAndRethrowsLast(PluginRequestFailureKind kind)
    {
        var attempts = 0;
        PluginRequestException? last = null;
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => LlmRequestRetryPolicy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            last = new PluginRequestException($"failure {attempts}", kind, retryAfter: TimeSpan.Zero);
            throw last;
        }, CancellationToken.None));
        Assert.Equal(3, attempts);
        Assert.Same(last, ex);
    }

    [Fact]
    public async Task OtherFailures_AreNeverRetried()
    {
        foreach (var kind in Enum.GetValues<PluginRequestFailureKind>().Except([
            PluginRequestFailureKind.Network, PluginRequestFailureKind.Timeout,
            PluginRequestFailureKind.RateLimit, PluginRequestFailureKind.ServerError]))
        {
            var attempts = 0;
            var failure = new PluginRequestException("failure", kind);
            var ex = await Assert.ThrowsAsync<PluginRequestException>(() => LlmRequestRetryPolicy.ExecuteAsync<string>(_ =>
            {
                attempts++;
                throw failure;
            }, CancellationToken.None));
            Assert.Same(failure, ex);
            Assert.Equal(1, attempts);
        }
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => LlmRequestRetryPolicy.ExecuteAsync<string>(_ =>
        {
            calls++;
            throw new IOException();
        }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Delay_UsesRetryAfterWithCapAndLinearFallback()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LlmRequestRetryPolicy.GetDelay(
            new PluginRequestException("rate", PluginRequestFailureKind.RateLimit, retryAfter: TimeSpan.FromHours(1)), 1));
        Assert.Equal(TimeSpan.Zero, LlmRequestRetryPolicy.GetDelay(
            new PluginRequestException("rate", PluginRequestFailureKind.RateLimit, retryAfter: TimeSpan.FromSeconds(-1)), 1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), LlmRequestRetryPolicy.GetDelay(
            new PluginRequestException("network", PluginRequestFailureKind.Network), 1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), LlmRequestRetryPolicy.GetDelay(
            new PluginRequestException("network", PluginRequestFailureKind.Network), 2));
    }

    [Fact]
    public async Task Cancellation_StopsBeforeAttemptAndDuringDelay()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LlmRequestRetryPolicy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new PluginRequestException("rate", PluginRequestFailureKind.RateLimit, retryAfter: TimeSpan.FromHours(1));
        }, cts.Token, (_, _, delay) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), delay);
            // ReSharper disable once AccessToDisposedClosure -- the callback runs synchronously inside the awaited ExecuteAsync, before the using disposes cts.
            cts.Cancel();
        }));
        Assert.Equal(1, attempts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LlmRequestRetryPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult("unexpected");
        }, cts.Token));
        Assert.Equal(1, attempts);
    }
}
