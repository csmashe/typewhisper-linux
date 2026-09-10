using System.Diagnostics;
using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

internal static class LlmRequestRetryPolicy
{
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        CancellationToken ct,
        Action<PluginRequestException, int, TimeSpan>? onRetry = null
    )
    {
        for (var retry = 1; ; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await attempt(ct);
            }
            catch (PluginRequestException ex) when (retry <= 2 && IsTransient(ex) && !ct.IsCancellationRequested)
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
        [EnumeratorCancellation] CancellationToken ct
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
            }, ct);

            if (!hasFirst)
                yield break;

            do
            {
                ct.ThrowIfCancellationRequested();
                yield return enumerator!.Current;
            } while (await enumerator.MoveNextAsync());
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync();
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
