using System.Diagnostics;
using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

internal static class LlmRequestRetryPolicy
{
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        CancellationToken ct,
        Action<PluginRequestException, int, TimeSpan>? onRetry = null,
        LlmRequestRetryBudget? budget = null
    )
    {
        budget ??= new LlmRequestRetryBudget();
        // Even after streaming exhausts the allowance, permit one batch compatibility attempt.
        var maxAttempts = Math.Max(1, budget.RemainingAttempts);
        for (var retry = 1; ; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                budget.RecordAttempt();
                return await attempt(ct);
            }
            catch (PluginRequestException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (PluginRequestException ex) when (retry < maxAttempts && ex.IsTransient && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = GetDelay(ex, retry);
                Trace.WriteLine($"[LlmRequestRetryPolicy] Retry {retry}: {ex.FailureKind}, delay {delay.TotalMilliseconds} ms");
                onRetry?.Invoke(ex, retry, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    internal static async IAsyncEnumerable<T> ExecuteStreamingAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> attempt,
        [EnumeratorCancellation] CancellationToken ct,
        LlmRequestRetryBudget? budget = null
    )
    {
        IAsyncEnumerator<T>? enumerator = null;
        try
        {
            var hasFirst = await ExecuteAsync(async token =>
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                    // ReSharper disable once RedundantAssignment -- clearing it keeps the finally below from disposing the stale enumerator a second time should the next line throw.
                    enumerator = null;
                }
                enumerator = attempt(token).GetAsyncEnumerator(token);
                return await enumerator.MoveNextAsync();
            }, ct, budget: budget);

            if (!hasFirst)
                yield break;

            do
            {
                ct.ThrowIfCancellationRequested();
                yield return enumerator!.Current;
            } while (await MoveNextOrThrowCanceledAsync(enumerator, ct));
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync();
        }
    }

    private static async ValueTask<bool> MoveNextOrThrowCanceledAsync<T>(
        IAsyncEnumerator<T> enumerator, CancellationToken ct)
    {
        try
        {
            return await enumerator.MoveNextAsync();
        }
        catch (PluginRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    internal static TimeSpan GetDelay(PluginRequestException failure, int retry)
    {
        var delay = failure.RetryAfter ?? TimeSpan.FromMilliseconds(250 * retry);
        return delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
    }

    private static bool IsTransient(PluginRequestException failure) => failure.FailureKind is
        PluginRequestFailureKind.Network or PluginRequestFailureKind.Timeout
        or PluginRequestFailureKind.RateLimit or PluginRequestFailureKind.ServerError;
}
