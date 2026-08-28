using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LlmStreamPumpTests
{
    [Fact]
    public async Task RunAsync_ConcatenatesAllDeltas_ReturnsFullText()
    {
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add);

        var result = await pump.RunAsync(
            Source("Hello", ", ", "world", "!"), CancellationToken.None);

        Assert.Equal("Hello, world!", result);
        Assert.False(pump.Faulted);
        Assert.True(pump.ReceivedAnyChunk);
        Assert.Equal("Hello, world!", emissions[^1]);
    }

    [Fact]
    public async Task RunAsync_Coalesces_HighFrequencyDeltas()
    {
        var emissions = new List<string>();
        // Huge interval => only the final flush fires regardless of delta count.
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));

        var deltas = Enumerable.Range(0, 1000).Select(_ => "x").ToArray();
        var result = await pump.RunAsync(Source(deltas), CancellationToken.None);

        Assert.Equal(new string('x', 1000), result);
        Assert.True(emissions.Count <= 2, $"expected heavy coalescing, got {emissions.Count} emissions");
        Assert.Equal(result, emissions[^1]);
    }

    [Fact]
    public async Task RunAsync_AlwaysEmitsFinalFlush()
    {
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));

        var result = await pump.RunAsync(Source("a", "b", "c"), CancellationToken.None);

        Assert.Equal("abc", result);
        Assert.Equal("abc", emissions[^1]);
    }

    [Fact]
    public async Task RunAsync_Cancellation_KeepsPartial_AndRethrows()
    {
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pump.RunAsync(CancellingSource(cts.Token), cts.Token));

        Assert.False(pump.Faulted);
        Assert.Equal("part1part2", emissions[^1]);
        return;

        async IAsyncEnumerable<string> CancellingSource(
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return "part1";
            yield return "part2";
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            yield return "never";
        }
    }

    [Fact]
    public async Task RunAsync_MidStreamFault_SetsFaulted_ReturnsPartial_NoThrow()
    {
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));

        var result = await pump.RunAsync(FaultingSource(), CancellationToken.None);

        Assert.True(pump.Faulted);
        Assert.Equal("good data", result);
        Assert.Equal("good data", emissions[^1]);
        return;

        async IAsyncEnumerable<string> FaultingSource()
        {
            yield return "good ";
            yield return "data";
            await Task.Yield();
            throw new HttpRequestException("boom");
        }
    }

    [Fact]
    public async Task RunAsync_DependencyCancellationWithLiveCaller_IsFaultResult()
    {
        var pump = new LlmStreamPump(_ => { });

        var result = await pump.RunAsync(
            FaultAfter("partial", new OperationCanceledException("provider canceled")),
            CancellationToken.None
        );

        Assert.Equal("partial", result);
        Assert.True(pump.Faulted);
        Assert.True(pump.ReceivedAnyChunk);
    }

    [Fact]
    public async Task RunAsync_PrivateTimeout_IsFaultResult()
    {
        var pump = new LlmStreamPump(_ => { });

        var result = await pump.RunAsync(
            FaultAfter("partial", new TimeoutException("provider deadline")),
            CancellationToken.None
        );

        Assert.Equal("partial", result);
        Assert.True(pump.Faulted);
    }

    [Fact]
    public async Task RunAsync_DependencyFaultRacingCallerCancellation_CallerWins()
    {
        using var cts = new CancellationTokenSource();
        var pump = new LlmStreamPump(_ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pump.RunAsync(RacingFault(cts), cts.Token));

        Assert.False(pump.Faulted);
        return;

        async IAsyncEnumerable<string> RacingFault(CancellationTokenSource source)
        {
            yield return "partial";
            await source.CancelAsync();
            throw new HttpRequestException("provider failed at cancellation");
        }
    }

    [Fact]
    public async Task RunAsync_EmptyStream_ReturnsEmpty()
    {
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));

        var result = await pump.RunAsync(Source(), CancellationToken.None);

        Assert.Equal("", result);
        Assert.False(pump.Faulted);
        Assert.False(pump.ReceivedAnyChunk);
        Assert.Empty(emissions);
    }

    [Fact]
    public async Task RunAsync_SingleEmptyChunk_ReceivedAnyChunkTrue_NoFallbackSignal()
    {
        // The toggle-off / default bulk-yield path yields exactly one chunk that
        // may legitimately be "". That must read as "the source produced output"
        // (ReceivedAnyChunk == true) so the caller does NOT re-run ProcessAsync,
        // even though the accumulated text is empty.
        var emissions = new List<string>();
        var pump = new LlmStreamPump(emissions.Add, TimeSpan.FromSeconds(10));

        var result = await pump.RunAsync(Source(""), CancellationToken.None);

        Assert.Equal("", result);
        Assert.False(pump.Faulted);
        Assert.True(pump.ReceivedAnyChunk);
        Assert.Empty(emissions);
    }

    private static async IAsyncEnumerable<string> Source(params string[] deltas)
    {
        foreach (var d in deltas)
        {
            await Task.Yield();
            yield return d;
        }
    }

    private static async IAsyncEnumerable<string> FaultAfter(
        string delta,
        Exception exception
    )
    {
        yield return delta;
        await Task.Yield();
        throw exception;
    }
}
