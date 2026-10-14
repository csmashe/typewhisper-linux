namespace TypeWhisper.Linux.Models;

/// <summary>
///     Terminal record for a completed dictation session, cached so polling
///     clients can resolve a <c>GET /v1/dictation/transcription</c> after the
///     session ends. <see cref="Status" /> distinguishes success ("ready")
///     from non-success terminal states ("failed", "canceled", "discarded")
///     so a polling client gets a real answer instead of looping on
///     "in_progress" forever when the session never produced text.
/// </summary>
public sealed record DictationSessionResult(
    int SessionId,
    string Status,
    string Text,
    string? RawText,
    string? Language,
    double DurationSeconds,
    string? EngineUsed,
    string? ModelUsed,
    string? Message = null
)
{
    // Available only after delivery finishes. Text can be ready before it is delivered.
    public DictationPerformance? Performance { get; init; }
}

public sealed record DictationPerformance(
    double TranscriptionMilliseconds,
    IReadOnlyDictionary<string, double> ProcessingMilliseconds,
    double QueueWaitMilliseconds,
    double DeliveryMilliseconds,
    double? StopToDeliveryMilliseconds,
    string DeliveryOutcome,
    int LiveCleanupRequests,
    bool LiveCleanupReused
);
