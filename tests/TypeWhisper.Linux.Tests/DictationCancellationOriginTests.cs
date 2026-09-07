using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationCancellationOriginTests
{
    [Fact]
    public async Task PromptAction_ProviderCancellationWithLiveToken_RetriesBatchExactlyOnce()
    {
        var batchCalls = 0;

        var result = await DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
            FaultingStream(new OperationCanceledException("provider canceled")),
            () =>
            {
                batchCalls++;
                return Task.FromResult("batch result");
            },
            _ => { },
            CancellationToken.None
        );

        Assert.Equal("batch result", result.Text);
        Assert.True(result.StreamFaulted);
        Assert.Equal(1, batchCalls);
    }

    [Fact]
    public async Task PromptAction_PrivateTimeout_RetriesBatchExactlyOnce()
    {
        var batchCalls = 0;

        var result = await DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
            FaultingStream(new TimeoutException("provider deadline")),
            () =>
            {
                batchCalls++;
                return Task.FromResult("batch result");
            },
            _ => { },
            CancellationToken.None
        );

        Assert.Equal("batch result", result.Text);
        Assert.True(result.StreamFaulted);
        Assert.Equal(1, batchCalls);
    }

    [Fact]
    public async Task PromptAction_GenuineCancellation_DoesNotRetryBatch()
    {
        using var cts = new CancellationTokenSource();
        var batchCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
                CancelingStream(cts),
                () =>
                {
                    batchCalls++;
                    return Task.FromResult("batch result");
                },
                _ => { },
                cts.Token
            ));

        Assert.Equal(0, batchCalls);
    }

    [Fact]
    public async Task PromptAction_DependencyFaultRacingCancellation_CancellationWinsWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var batchCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
                RacingFaultStream(cts),
                () =>
                {
                    batchCalls++;
                    return Task.FromResult("batch result");
                },
                _ => { },
                cts.Token
            ));

        Assert.Equal(0, batchCalls);
    }

    [Fact]
    public async Task SpokenCommand_ProviderCancellationBeforeFirstTypedChunk_RetriesBatch()
    {
        var batchCalls = 0;

        var faulted = await DictationOrchestrator.RecoverSpokenCommandStreamFaultAsync(
            new OperationCanceledException("provider canceled"),
            typedAnything: false,
            () =>
            {
                batchCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None
        );

        Assert.False(faulted);
        Assert.Equal(1, batchCalls);
    }

    [Fact]
    public async Task SpokenCommand_ProviderCancellationAfterVisiblePrefix_DoesNotRetry()
    {
        var batchCalls = 0;

        var faulted = await DictationOrchestrator.RecoverSpokenCommandStreamFaultAsync(
            new OperationCanceledException("provider canceled"),
            typedAnything: true,
            () =>
            {
                batchCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None
        );

        Assert.True(faulted);
        Assert.Equal(0, batchCalls);
    }

    [Fact]
    public async Task SpokenCommand_PrivateTimeoutBeforeTyping_RetriesBatch()
    {
        var batchCalls = 0;

        var faulted = await DictationOrchestrator.RecoverSpokenCommandStreamFaultAsync(
            new TimeoutException("provider deadline"),
            typedAnything: false,
            () =>
            {
                batchCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None
        );

        Assert.False(faulted);
        Assert.Equal(1, batchCalls);
    }

    [Fact]
    public async Task SpokenCommand_CancellationRacingProviderFault_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var batchCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DictationOrchestrator.RecoverSpokenCommandStreamFaultAsync(
                new TimeoutException("provider deadline"),
                typedAnything: false,
                () =>
                {
                    batchCalls++;
                    return Task.FromResult(true);
                },
                cts.Token
            ));

        Assert.Equal(0, batchCalls);
    }

    [Fact]
    public void PostStop_DependencyCancellationWithLiveToken_IsFailed()
    {
        Assert.Equal(
            "failed",
            DictationOrchestrator.ClassifyPostStopTerminal(
                new OperationCanceledException("dependency canceled"),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public void PostStop_GenuineCancellation_IsCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(
            "canceled",
            DictationOrchestrator.ClassifyPostStopTerminal(
                new OperationCanceledException(cts.Token),
                cts.Token
            )
        );
    }

    [Fact]
    public void PostStop_PrivateTimeoutWithLiveToken_IsFailed()
    {
        Assert.Equal(
            "failed",
            DictationOrchestrator.ClassifyPostStopTerminal(
                new TimeoutException("dependency deadline"),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public void PostStop_FaultRacingCancellation_IsCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(
            "canceled",
            DictationOrchestrator.ClassifyPostStopTerminal(
                new HttpRequestException("dependency fault"),
                cts.Token
            )
        );
    }

    private static async IAsyncEnumerable<string> FaultingStream(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return "partial";
        await Task.Yield();
        throw exception;
    }

    private static async IAsyncEnumerable<string> CancelingStream(
        CancellationTokenSource cts
    )
    {
        yield return "partial";
        await cts.CancelAsync();
        cts.Token.ThrowIfCancellationRequested();
    }

    private static async IAsyncEnumerable<string> RacingFaultStream(
        CancellationTokenSource cts
    )
    {
        yield return "partial";
        await cts.CancelAsync();
        throw new HttpRequestException("provider failed during cancellation");
    }
}
