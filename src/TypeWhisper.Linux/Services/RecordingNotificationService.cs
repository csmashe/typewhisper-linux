using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Recording indicator for tiling window managers (Hyprland/Sway/…), where
///     the floating overlay is the wrong primitive. Instead of the overlay we
///     raise a desktop notification on <c>org.freedesktop.Notifications</c> via
///     gdbus (glib — present on every desktop with a notification daemon: mako,
///     dunst, GNOME, KDE). The notification is persistent (expire_timeout 0) so
///     it stays up for the whole recording, then we close it by id when
///     recording stops.
///     <para>
///         No-op on full desktop environments (GNOME/KDE/Cinnamon/…), which keep
///         the overlay — see <see cref="DesktopDetector.UsesNotificationRecordingIndicator" />.
///     </para>
/// </summary>
public sealed partial class RecordingNotificationService : IDisposable
{
    private const string Summary = "🔴 Recording";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly DictationOrchestrator _dictation;
    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;
    private readonly bool _enabled;
    private readonly object _gate = new();

    private bool _wasRecording;
    private uint _activeId;

    // Monotonic state-change counter. Every Start/Stop edge bumps it under
    // _gate and hands the new value to the show/close it schedules. Because
    // ShowAsync/CloseAsync are fire-and-forget and await a multi-second gdbus
    // call, a rapid Start→Stop can finish out of order: a Stop's CloseAsync
    // can read _activeId == 0 (the in-flight Show hasn't stored its id yet)
    // and no-op, then the Show lands its id and the "🔴 Recording" popup
    // sticks forever. Each handler re-checks this generation after its await
    // and bails (closing its own just-created id) if a newer edge superseded
    // it — so the last edge always wins.
    private uint _generation;

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

    /// <summary>
    ///     The recording notification's body text. The "how to stop" hint depends
    ///     on the recording mode: push-to-talk ends on release, toggle ends on a
    ///     second press, and hybrid allows either. Shared so the Appearance
    ///     settings preview can't drift from what's actually shown.
    /// </summary>
    public static string BodyFor(RecordingMode mode) =>
        mode switch
        {
            RecordingMode.Toggle => "Speak now — press the shortcut again to stop",
            RecordingMode.PushToTalk => "Speak now — release to insert",
            _ => "Speak now — release, or press the shortcut again, to insert",
        };

    private string ResolveBody() => BodyFor(_settings.Current.Mode);

    public void Initialize()
    {
        if (!_enabled)
        {
            return;
        }

        _dictation.OverlayStateChanged += OnOverlayStateChanged;
    }

    private void OnOverlayStateChanged(object? sender, DictationOverlayState state)
    {
        // Edge-trigger on the recording flag — OverlayStateChanged fires many
        // times within a single recording (partial text, audio levels).
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
        // Reuse the previous id as replaces_id so a notification that lingered
        // (e.g. a rapid stop that raced an in-flight show) is replaced in place
        // rather than stacking a second popup.
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
                        "call",
                        "--session",
                        "--dest",
                        "org.freedesktop.Notifications",
                        "--object-path",
                        "/org/freedesktop/Notifications",
                        "--method",
                        "org.freedesktop.Notifications.Notify",
                        "TypeWhisper",
                        replaceId.ToString(),
                        ResolveIconPath(),
                        Summary,
                        ResolveBody(),
                        "[]", // actions
                        "{}", // hints
                        "0", // expire_timeout 0 → stay up until we close it
                    },
                    timeout: CallTimeout
                )
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return;
            }

            // gdbus prints "(uint32 N,)" — anchor on "uint32 " so we don't grab
            // the "32" out of the type name itself.
            var match = NotificationIdRegex().Match(result.StandardOutput);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, out var id))
            {
                return;
            }

            bool superseded;
            lock (_gate)
            {
                // A newer Start/Stop edge fired while this Notify was in flight.
                // Recording has since stopped (or restarted with its own show),
                // so don't adopt this id — dismiss it ourselves below.
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
            // Ignore a late Stop that a newer Start has already superseded —
            // closing here would dismiss the notification the new recording
            // just put up and leave it with none.
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
                        "call",
                        "--session",
                        "--dest",
                        "org.freedesktop.Notifications",
                        "--object-path",
                        "/org/freedesktop/Notifications",
                        "--method",
                        "org.freedesktop.Notifications.CloseNotification",
                        id.ToString(),
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
        // Prefer the icon the installer drops under the icon theme; fall back to
        // the bundled resource shipped next to the binary; last resort a themed
        // name (notification daemons resolve it from the icon theme).
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

    [GeneratedRegex(@"uint32 (\d+)")]
    private static partial Regex NotificationIdRegex();
}
