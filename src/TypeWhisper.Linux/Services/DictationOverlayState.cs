namespace TypeWhisper.Linux.Services;

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
