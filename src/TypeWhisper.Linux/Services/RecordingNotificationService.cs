using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Recording indicator for tiling WMs (Hyprland/Sway/…) via a persistent
///     <c>org.freedesktop.Notifications</c> desktop notification (expire_timeout 0),
///     closed by id when recording stops. No-op on full DEs (GNOME/KDE/Cinnamon)
///     which use the overlay — see <see cref="DesktopDetector.UsesNotificationRecordingIndicator" />.
/// </summary>
public sealed partial class RecordingNotificationService : IDisposable
{
    private const string Summary = "🔴 Recording";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly DictationOrchestrator _dictation;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly IProcessRunner _runner;
    private readonly ISettingsService _settings;
    private uint _activeId;

    // Monotonic counter bumped on every Start/Stop edge. ShowAsync/CloseAsync are
    // fire-and-forget and await a multi-second gdbus call, so a rapid Start→Stop
    // can finish out of order. Each handler re-checks the generation after its await
    // and bails (closing its own just-created id) if superseded — last edge wins.
    private uint _generation;

    private bool _wasRecording;

    public RecordingNotificationService(
        DictationOrchestrator dictation,
        ISettingsService settings,
        IProcessRunner runner
    )
    {
        _dictation = dictation;
        _settings = settings;
        _runner = runner;
        _enabled = DesktopDetector.UsesNotificationRecordingIndicator();
    }

    public void Dispose()
    {
        if (_enabled)
        {
            _dictation.OverlayStateChanged -= OnOverlayStateChanged;
        }

        // Teardown — supersede any in-flight show and dismiss whatever is up.
        uint generation;
        lock (_gate)
        {
            generation = ++_generation;
        }

        _ = CloseAsync(generation);
    }

    /// <summary>
    ///     Notification body text for the given recording mode. Shared so the
    ///     Appearance settings preview stays in sync with what's actually shown.
    /// </summary>
    public static string BodyFor(RecordingMode mode)
    {
        return mode switch
        {
            RecordingMode.Toggle => "Speak now — press the shortcut again to stop",
            RecordingMode.PushToTalk => "Speak now — release to insert",
            _ => "Speak now — release, or press the shortcut again, to insert"
        };
    }

    public void Initialize()
    {
        if (!_enabled)
        {
            return;
        }

        _dictation.OverlayStateChanged += OnOverlayStateChanged;
    }

    private string ResolveBody()
    {
        return BodyFor(_settings.Current.Mode);
    }

    private void OnOverlayStateChanged(object? sender, DictationOverlayState state)
    {
        // Edge-trigger: OverlayStateChanged fires many times per recording (partial text, levels).
        if (state.IsRecording == _wasRecording)
        {
            return;
        }

        _wasRecording = state.IsRecording;
        uint generation;
        lock (_gate)
        {
            generation = ++_generation;
        }

        if (state.IsRecording)
        {
            _ = ShowAsync(generation);
        }
        else
        {
            _ = CloseAsync(generation);
        }
    }

    private async Task ShowAsync(uint generation)
    {
        // Use previous id as replaces_id so a lingered notification is replaced
        // in-place rather than stacking a second popup.
        uint replaceId;
        lock (_gate)
        {
            replaceId = _activeId;
        }

        try
        {
            var result = await _runner
                .RunAsync(
                    "gdbus",
                    new[]
                    {
                        "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path",
                        "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                        "TypeWhisper", replaceId.ToString(), ResolveIconPath(), Summary, ResolveBody(), "[]", // actions
                        "{}", // hints
                        "0" // expire_timeout 0 → stay up until we close it
                    },
                    timeout: CallTimeout
                )
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return;
            }

            // gdbus prints "(uint32 N,)" — anchor on "uint32 " to avoid matching the "32" in the type name.
            var match = NotificationIdRegex().Match(result.StandardOutput);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, out var id))
            {
                return;
            }

            bool superseded;
            lock (_gate)
            {
                // A newer edge fired while Notify was in flight — dismiss this id.
                superseded = generation != _generation;
                if (!superseded)
                {
                    _activeId = id;
                }
            }

            if (superseded)
            {
                await CloseByIdAsync(id).ConfigureAwait(false);
            }
        }
        catch
        {
            // Notifications are purely advisory — never let one disrupt dictation.
        }
    }

    private async Task CloseAsync(uint generation)
    {
        uint id;
        lock (_gate)
        {
            // A newer Start superseded this Stop — closing would dismiss the new recording's notification.
            if (generation != _generation)
            {
                return;
            }

            id = _activeId;
            _activeId = 0;
        }

        if (id == 0)
        {
            return;
        }

        await CloseByIdAsync(id).ConfigureAwait(false);
    }

    private async Task CloseByIdAsync(uint id)
    {
        try
        {
            await _runner
                .RunAsync(
                    "gdbus",
                    new[]
                    {
                        "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path",
                        "/org/freedesktop/Notifications", "--method",
                        "org.freedesktop.Notifications.CloseNotification", id.ToString()
                    },
                    timeout: CallTimeout
                )
                .ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    private static string ResolveIconPath()
    {
        // Prefer installer-dropped icon theme path; then bundled resource;
        // last resort a themed name notification daemons resolve from the icon theme.
        var installed = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "icons",
            "hicolor",
            "128x128",
            "apps",
            "typewhisper.png"
        );
        if (File.Exists(installed))
        {
            return installed;
        }

        var bundled = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-128.png");
        return File.Exists(bundled) ? bundled : "typewhisper";
    }

    [GeneratedRegex(@"uint32 (\d+)")]
    private static partial Regex NotificationIdRegex();
}