using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiApiHelperTests
{
    [Fact]
    public async Task SendWithErrorHandlingAsync_PrivateTaskCancellation_IsTimeoutException()
    {
        using var httpClient = new HttpClient(
            new AsyncHandler((_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("HTTP client deadline")
            ))
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(
                httpClient,
                request,
                CancellationToken.None
            ));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
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
    public async Task ChatStreamingRequest_PrivateTaskCancellation_IsTimeoutException()
    {
        using var httpClient = new HttpClient(
            new AsyncHandler((_, _) => Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("HTTP client deadline")
            ))
        );

        await Assert.ThrowsAsync<TimeoutException>(async () =>
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
