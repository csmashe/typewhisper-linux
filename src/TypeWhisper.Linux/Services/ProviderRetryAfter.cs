namespace TypeWhisper.Linux.Services;

/// <summary>The one Retry-After rule shared by the LLM and transcription retry paths.</summary>
internal static class ProviderRetryAfter
{
    private static readonly TimeSpan s_cap = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The wait before retrying, or null when the provider asked for longer than 10 seconds:
    ///     it is overloaded for longer than a dictation should wait, so its error surfaces at once.
    /// </summary>
    internal static TimeSpan? Honour(TimeSpan retryAfter) =>
        retryAfter > s_cap ? null : retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
}
