namespace TypeWhisper.Linux.Services;

/// <summary>
///     Immutable snapshot of the dictation overlay's visual state. Published via
///     <c>OverlayStateChanged</c> and consumed by the overlay window and tray tooltip.
///     Business logic stays in the orchestrator; only UI-relevant fields belong here.
/// </summary>
public sealed record DictationOverlayState
{
    public static DictationOverlayState Hidden { get; } = new();

    public bool IsOverlayVisible { get; init; }
    public bool ShowFeedback { get; init; }
    public bool FeedbackIsError { get; init; }
    public bool IsRecording { get; init; }
    public string StatusText { get; init; } = "Ready";
    public string? PartialText { get; init; }

    /// <summary>
    ///     Accumulated LLM response text, streamed token-by-token during a
    ///     prompt-action step. Null when no LLM step is running.
    /// </summary>
    public string? LlmResponseText { get; init; }

    public string? FeedbackText { get; init; }
    public string? ActiveProfileName { get; init; }
    public string? ActiveAppName { get; init; }
    public DateTime? SessionStartedAtUtc { get; init; }
}