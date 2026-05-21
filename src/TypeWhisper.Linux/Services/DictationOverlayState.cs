namespace TypeWhisper.Linux.Services;

/// <summary>
///     Immutable snapshot of the dictation overlay's visual state. Published
///     by <c>DictationOrchestrator</c> and <c>TransformSelectionService</c>
///     via <c>OverlayStateChanged</c>; consumed by the overlay window and
///     the tray tooltip. Only carry UI-relevant fields here — business logic
///     lives in the orchestrator.
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
    public string? FeedbackText { get; init; }
    public string? ActiveProfileName { get; init; }
    public string? ActiveAppName { get; init; }
    public DateTime? SessionStartedAtUtc { get; init; }
}