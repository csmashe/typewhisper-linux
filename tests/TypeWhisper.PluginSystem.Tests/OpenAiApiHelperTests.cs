using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK;
using System.Net;
using System.Net.Http.Headers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiApiHelperTests
{
    [Fact]
    public void BodyCancellation_PrefersCallerToken()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            OpenAiApiHelper.MapBodyReadFailure(new OperationCanceledException(), caller.Token));
        Assert.Equal(PluginRequestFailureKind.Timeout,
            OpenAiApiHelper.MapBodyReadFailure(new OperationCanceledException(), CancellationToken.None).FailureKind);
    }

    [Fact]
    public async Task QuerySecrets_RedactsEncodedAndDecodedForms()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) =>
            throw new HttpRequestException("key=abc%2Fdef and abc/def")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?key=abc%2Fdef");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.DoesNotContain("abc%2Fdef", error.Message);
        Assert.DoesNotContain("abc/def", error.Message);
    }

    [Fact]
    public async Task SendWithErrorHandlingAsync_PrivateTaskCancellation_IsClassifiedTimeout()
    {
        using var httpClient = new HttpClient(
            new AsyncHandler((_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("HTTP client deadline")
            ))
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

        var exception = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(
                httpClient,
                request,
                CancellationToken.None
            ));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
        Assert.Equal(PluginRequestFailureKind.Timeout, exception.FailureKind);
    }

    [Fact]
    public async Task SendWithErrorHandlingAsync_GenuineCallerCancellation_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var httpClient = new HttpClient(
            new AsyncHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct))
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, cts.Token));
    }

    [Fact]
    public async Task SendWithErrorHandlingAsync_PrivateAndCallerCancellationRace_CallerWins()
    {
        using var cts = new CancellationTokenSource();
        using var httpClient = new HttpClient(
            new AsyncHandler((_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the
                // awaited call below, which completes before the using-scope disposes cts.
                cts.Cancel();
                return Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("both requested")
                );
            })
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, cts.Token));
    }

    [Fact]
    public async Task ChatStreamingRequest_PrivateTaskCancellation_IsClassifiedTimeout()
    {
        using var httpClient = new HttpClient(
            new AsyncHandler((_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("HTTP client deadline")
            ))
        );

        var exception = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var _ in OpenAiChatHelper.SendChatCompletionStreamingAsync(
                               httpClient,
                               "http://localhost",
                               "key",
                               "model",
                               "system",
                               "user",
                               CancellationToken.None
                           ))
            {
                // Draining the stream is what surfaces the error; the chunks themselves are moot.
            }
        });
        Assert.Equal(PluginRequestFailureKind.Timeout, exception.FailureKind);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(408, PluginRequestFailureKind.Timeout)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(500, PluginRequestFailureKind.ServerError)]
    [InlineData(599, PluginRequestFailureKind.ServerError)]
    [InlineData(400, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(404, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(302, PluginRequestFailureKind.Unknown)]
    public async Task HttpFailure_IsClassifiedAndSanitized(int status, PluginRequestFailureKind kind)
    {
        using var client = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"failure secret-key \"}}"),
            })));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.Equal(kind, ex.FailureKind);
        Assert.Equal(status, ex.HttpStatusCode);
        Assert.DoesNotContain("secret-key", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfter_ParsesDeltaAndDate(bool date)
    {
        using var client = new HttpClient(new AsyncHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = date
                ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.InRange(ex.RetryAfter!.Value.TotalSeconds, 28, 30);
    }

    [Fact]
    public async Task RetryAfter_PastDateIsClampedToZero()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(-1));
            return Task.FromResult(response);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task NetworkFailure_IsClassifiedWithoutLeakingKey()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) =>
            throw new HttpRequestException("connection failed secret-key")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        request.Headers.Add("x-api-key", "secret-key");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Network, ex.FailureKind);
        Assert.DoesNotContain("secret-key", ex.Message);
    }

    [Fact]
    public async Task ErrorBody_IsCollapsedAndCappedAfterRedaction()
    {
        var secret = new string('s', 350);
        using var client = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"{{\"error\":{{\"message\":\"{secret} failure\\n\\t {new string('x', 500)}\"}}}}"),
            })));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.Equal(300, ex.Message.Length);
        Assert.EndsWith("…", ex.Message);
        Assert.DoesNotContain(new string('s', 10), ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
        Assert.DoesNotContain("\t", ex.Message);
    }

    [Fact]
    public async Task PrivateOperationCancellation_IsTimeout()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) => throw new OperationCanceledException("deadline")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Timeout, ex.FailureKind);
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => responder(request, cancellationToken);
    }
}
