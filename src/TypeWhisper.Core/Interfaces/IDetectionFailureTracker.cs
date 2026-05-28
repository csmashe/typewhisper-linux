namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Tracks consecutive active-window detection failures so the UI can show
///     transient toasts for one-off blips and escalate to a persistent banner
///     once failures pass a threshold. Used by the dictation overlay and the
///     settings UI to surface compositor-integration trouble (missing helper
///     binaries, denied portals) without flooding the user with toasts.
/// </summary>
public interface IDetectionFailureTracker
{
    /// <summary>Number of consecutive failures since the last <see cref="RecordSuccess" />.</summary>
    int ConsecutiveFailures { get; }

    /// <summary>
    ///     True once <see cref="ConsecutiveFailures" /> has crossed the
    ///     tracker's banner threshold. Resets to false on the next successful
    ///     detection.
    /// </summary>
    bool ShouldShowPersistentBanner { get; }

    /// <summary>Reason string from the most recent failure, or <c>null</c> if no failures have been recorded.</summary>
    string? LastFailureReason { get; }

    /// <summary>Resets the consecutive-failure counter and clears the banner state.</summary>
    void RecordSuccess();

    /// <summary>
    ///     Increments <see cref="ConsecutiveFailures" />, updates
    ///     <see cref="LastFailureReason" />, recomputes
    ///     <see cref="ShouldShowPersistentBanner" />, and fires <see cref="OnFailure" />.
    /// </summary>
    void RecordFailure(string compositor, string reason);

    /// <summary>Raised on every <see cref="RecordFailure" /> call, including the one that flips the banner state.</summary>
    event EventHandler<DetectionFailureEvent>? OnFailure;
}

/// <summary>
///     Payload for <see cref="IDetectionFailureTracker.OnFailure" />. Carries the
///     failure count after the increment, the compositor identifier and reason
///     supplied to <see cref="IDetectionFailureTracker.RecordFailure" />, and the
///     post-update banner state so subscribers can branch on toast-vs-banner
///     without re-querying the tracker.
/// </summary>
public sealed record DetectionFailureEvent(
    int ConsecutiveFailures,
    string Compositor,
    string Reason,
    bool ShouldShowPersistentBanner
);