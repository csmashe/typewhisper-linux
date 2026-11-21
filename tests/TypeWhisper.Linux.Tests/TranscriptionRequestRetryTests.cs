using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranscriptionRequestRetryTests
{
    private readonly List<TimeSpan> _delays = [];

    private TranscriptionRequestRetry CreateRetry() =>
        new((delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(PluginRequestFailureKind.Network)]
    [InlineData(PluginRequestFailureKind.Timeout)]
    [InlineData(PluginRequestFailureKind.RateLimit)]
    [InlineData(PluginRequestFailureKind.ServerError)]
    public async Task TransientFailure_RetriesTwiceWithBackoffAndRethrowsLast(PluginRequestFailureKind kind)
    {
        var attempts = 0;
        PluginRequestException? last = null;
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
        {
            attempts++;
            last = new PluginRequestException($"failure {attempts}", kind);
            throw last;
        }, CancellationToken.None));

        Assert.Equal(3, attempts);
        Assert.Same(last, ex);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
    }

    [Fact]
    public async Task TransientFailure_ThenSuccess_ReturnsResult()
    {
        var attempts = 0;
        var result = await CreateRetry().RunAsync("p", 100, _ => ++attempts == 1
            ? throw new PluginRequestException("busy", PluginRequestFailureKind.ServerError, 503)
            : Task.FromResult("text"), CancellationToken.None);

        Assert.Equal("text", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RetryAfter_IsHonouredUpToTheCap()
    {
        var attempts = 0;
        await CreateRetry().RunAsync("p", 100, _ => ++attempts == 1
            ? throw new PluginRequestException("rate", PluginRequestFailureKind.RateLimit, 429, TimeSpan.FromSeconds(10))
            : Task.FromResult("text"), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(10)], _delays);
    }

    [Fact]
    public async Task RetryAfterAboveCap_SurfacesAtOnce()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<PluginRequestException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
        {
            attempts++;
            throw new PluginRequestException("rate", PluginRequestFailureKind.RateLimit, 429, TimeSpan.FromSeconds(11));
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task OtherFailures_AreNeverRetried()
    {
        foreach (var kind in Enum.GetValues<PluginRequestFailureKind>().Except([
            PluginRequestFailureKind.Network, PluginRequestFailureKind.Timeout,
            PluginRequestFailureKind.RateLimit, PluginRequestFailureKind.ServerError]))
        {
            var attempts = 0;
            await Assert.ThrowsAsync<PluginRequestException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
            {
                attempts++;
                throw new PluginRequestException("failure", kind);
            }, CancellationToken.None));
            Assert.Equal(1, attempts);
        }

        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
        {
            calls++;
            throw new IOException();
        }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task OverloadStatus_IsRetriedEvenWhenThePluginCalledItPermanent(int status)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<PluginRequestException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
        {
            attempts++;
            throw new PluginRequestException("busy", PluginRequestFailureKind.Unknown, status, isTransient: false);
        }, CancellationToken.None));

        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(PluginRequestFailureKind.Timeout, null)]
    [InlineData(PluginRequestFailureKind.ServerError, 504)]
    public async Task Timeout_OnLargeUpload_IsNotRetried(PluginRequestFailureKind kind, int? status)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<PluginRequestException>(() => CreateRetry().RunAsync<string>(
            "p", TranscriptionRequestRetry.TimeoutRetryMaximumBytes + 1, _ =>
            {
                attempts++;
                throw new PluginRequestException("timeout", kind, status);
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_DuringDelay_StopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        // ReSharper disable once AccessToDisposedClosure -- the delay runs inside the awaited RunAsync, before cts is disposed.
        var retry = new TranscriptionRequestRetry((_, token) =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.RunAsync<string>("p", 100, _ =>
        {
            attempts++;
            throw new PluginRequestException("busy", PluginRequestFailureKind.ServerError);
        }, cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FailureAfterCallerCancelled_SurfacesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRetry().RunAsync<string>("p", 100, _ =>
        {
            attempts++;
            // ReSharper disable once AccessToDisposedClosure -- runs inside the awaited RunAsync, before cts is disposed.
            cts.Cancel();
            throw new PluginRequestException("busy", PluginRequestFailureKind.ServerError);
        }, cts.Token));

        Assert.Equal(1, attempts);
        Assert.Empty(_delays);
    }
}
