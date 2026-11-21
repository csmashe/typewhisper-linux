using System.Diagnostics;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Sends a whole batch transcription upload again after a transient provider failure: at most two
///     retries, after the provider's Retry-After when <see cref="ProviderRetryAfter" /> honours it,
///     otherwise after one and then two seconds.
/// </summary>
/// <remarks>
///     Only whole uploads belong here, never streams or calls that already reported partial output.
///     Local engines never throw transient request failures, so they get one attempt.
/// </remarks>
internal sealed class TranscriptionRequestRetry
{
    private const int MaximumRetries = 2;

    /// <summary>
    ///     About 32 seconds of 16 kHz mono PCM16. A larger upload that timed out has already waited out
    ///     the provider's timeout, so retrying it would add minutes to the dictation.
    /// </summary>
    internal const int TimeoutRetryMaximumBytes = 1024 * 1024;

    private static readonly TimeSpan[] s_backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal TranscriptionRequestRetry(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
    }

    internal static TranscriptionRequestRetry Default { get; } = new();

    internal async Task<T> RunAsync<T>(
        string providerId,
        int uploadBytes,
        Func<CancellationToken, Task<T>> request,
        CancellationToken ct
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await request(ct);
            }
            catch (PluginRequestException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (PluginRequestException ex) when (
                attempt <= MaximumRetries
                && IsRetryable(ex, uploadBytes)
                && DelayFor(ex, attempt) is { } delay
            )
            {
                Trace.WriteLine(
                    $"[TranscriptionRetry] Provider '{providerId}' retry {attempt}: {ex.FailureKind}"
                        + (ex.HttpStatusCode is { } status ? $" (HTTP {status})" : "")
                        + $", delay {delay.TotalMilliseconds} ms"
                );
                await _delay(delay, ct);
            }
        }
    }

    /// <summary>
    ///     The SDK's transient flag, or the statuses providers answer with while overloaded even when a
    ///     plugin classified them differently. An empty response is not retried: silence would only come
    ///     back empty again.
    /// </summary>
    private static bool IsRetryable(PluginRequestException failure, int uploadBytes)
    {
        // A gateway timeout (504) is a timeout too, whatever kind the plugin gave it.
        if (failure is { FailureKind: PluginRequestFailureKind.Timeout } or { HttpStatusCode: 504 }
            && uploadBytes > TimeoutRetryMaximumBytes)
        {
            return false;
        }

        return failure.HttpStatusCode is 429 or 502 or 503 or 504
            || failure is
            {
                IsTransient: true,
                FailureKind: PluginRequestFailureKind.Network
                    or PluginRequestFailureKind.Timeout
                    or PluginRequestFailureKind.RateLimit
                    or PluginRequestFailureKind.ServerError,
            };
    }

    /// <summary>The wait after the given failed attempt, or null when the provider asked for too long a wait.</summary>
    private static TimeSpan? DelayFor(PluginRequestException failure, int attempt) =>
        failure.RetryAfter is { } retryAfter
            ? ProviderRetryAfter.Honour(retryAfter)
            : s_backoff[Math.Clamp(attempt, 1, s_backoff.Length) - 1];
}
