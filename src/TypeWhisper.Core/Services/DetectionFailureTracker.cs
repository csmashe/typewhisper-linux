using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DetectionFailureTracker : IDetectionFailureTracker
{
    private const int BannerThreshold = 10;

    private readonly IErrorLogService _errorLog;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private string? _lastFailureReason;

    public DetectionFailureTracker(IErrorLogService errorLog)
    {
        _errorLog = errorLog;
    }

    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    public bool ShouldShowPersistentBanner
    {
        get { lock (_lock) return _consecutiveFailures >= BannerThreshold; }
    }

    public string? LastFailureReason
    {
        get { lock (_lock) return _lastFailureReason; }
    }

    public event EventHandler<DetectionFailureEvent>? OnFailure;

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _lastFailureReason = null;
        }
    }

    public void RecordFailure(string compositor, string reason)
    {
        var augmented = AugmentReason(compositor, reason);

        int failures;
        bool shouldShowBanner;
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureReason = augmented;
            failures = _consecutiveFailures;
            shouldShowBanner = failures >= BannerThreshold;
        }

        _errorLog.AddEntry($"Window detection failed on {compositor}: {augmented}", ErrorCategory.General);

        OnFailure?.Invoke(this, new DetectionFailureEvent(failures, compositor, augmented, shouldShowBanner));
    }

    private static string AugmentReason(string compositor, string reason) => compositor switch
    {
        // Modern GNOME (41+) restricts the built-in org.gnome.Shell.Introspect
        // API to trusted clients, so unprivileged apps see AccessDenied. The
        // user-installed "Window Calls" extension exposes the same data via
        // org.gnome.Shell.Extensions.Windows on the session bus.
        "gnome-shell" or "gnome-window-calls" =>
            $"{reason}. Install the \"Window Calls\" GNOME Shell extension to enable window detection on GNOME Wayland.",
        "kwin" => $"{reason}. Install kdotool to enable detection.",
        "hyprland" or "sway" => $"{reason}. Compositor command failed unexpectedly.",
        "xdotool" => $"{reason}. xdotool only works on X11/XWayland — install a Wayland-native compositor for better detection.",
        _ => reason
    };
}
