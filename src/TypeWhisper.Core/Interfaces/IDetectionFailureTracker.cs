namespace TypeWhisper.Core.Interfaces;

public interface IDetectionFailureTracker
{
    int ConsecutiveFailures { get; }
    bool ShouldShowPersistentBanner { get; }
    string? LastFailureReason { get; }
    event EventHandler<DetectionFailureEvent>? OnFailure;

    void RecordSuccess();
    void RecordFailure(string compositor, string reason);
}

public sealed record DetectionFailureEvent(
    int ConsecutiveFailures,
    string Compositor,
    string Reason,
    bool ShouldShowPersistentBanner);
