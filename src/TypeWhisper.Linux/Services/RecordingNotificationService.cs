using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

internal interface IRecordingNotificationStateSource
{
    event EventHandler<OverlayPresentationChangedEventArgs>? PresentationChanged;
}

/// <summary>
///     Complete dictation state and feedback surface for notification-indicator
///     WMs (Hyprland/Sway/River/Niri) via <c>org.freedesktop.Notifications</c>.
///     No-op on full DEs (GNOME/KDE/Cinnamon), which use the overlay — see
///     <see cref="DesktopDetector.UsesNotificationRecordingIndicator" />.
/// </summary>
public sealed partial class RecordingNotificationService : IDisposable
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(3);

    private readonly bool _enabled;
    private readonly Lock _gate = new();
    private readonly IProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly IRecordingNotificationStateSource _stateSource;
    private uint _activeId;
    private NotificationPresentation? _desiredPresentation;
    private bool _disposed;
    private uint _desiredVersion;
    private TaskCompletionSource? _idleCompletion;
    private bool _initialized;
    private long _lastPresentationRevision;
    private bool _workerRunning;

    public RecordingNotificationService(
        OverlayCoordinator overlayCoordinator,
        ISettingsService settings,
        IProcessRunner runner
    )
        : this(
            new CoordinatorPresentationSource(overlayCoordinator),
            settings,
            runner,
            DesktopDetector.UsesNotificationRecordingIndicator()
        )
    {
    }

    internal RecordingNotificationService(
        IRecordingNotificationStateSource stateSource,
        ISettingsService settings,
        IProcessRunner runner,
        bool enabled
    )
    {
        _stateSource = stateSource;
        _settings = settings;
        _runner = runner;
        _enabled = enabled;
    }

    internal RecordingNotificationService(
        OverlayCoordinator overlayCoordinator,
        ISettingsService settings,
        IProcessRunner runner,
        bool enabled
    )
        : this(new CoordinatorPresentationSource(overlayCoordinator), settings, runner, enabled)
    {
    }

    public void Dispose()
    {
        bool startWorker;
        lock (_gate)
        {
            if (!_enabled || _disposed)
            {
                return;
            }

            _disposed = true;
            if (_initialized)
            {
                _stateSource.PresentationChanged -= OnPresentationChanged;
                _initialized = false;
            }

            if (_desiredPresentation is not null || _activeId != 0)
            {
                _desiredPresentation = null;
                _desiredVersion++;
            }

            startWorker = StartWorkerIfNeededLocked();
        }

        if (startWorker)
        {
            _ = DispatchLoopAsync();
        }
    }

    /// <summary>
    ///     Notification body text for the given recording mode. Shared so the
    ///     Appearance settings preview stays in sync with what's actually shown.
    /// </summary>
    public static string BodyFor(RecordingMode mode)
    {
        return mode switch
        {
            RecordingMode.Toggle => Loc.Instance["Notify.BodyToggle"],
            RecordingMode.PushToTalk => Loc.Instance["Notify.BodyPushToTalk"],
            _ => Loc.Instance["Notify.BodyHybrid"],
        };
    }

    public void Initialize()
    {
        lock (_gate)
        {
            if (!_enabled || _initialized || _disposed)
            {
                return;
            }

            _stateSource.PresentationChanged += OnPresentationChanged;
            _initialized = true;
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _workerRunning ? _idleCompletion!.Task : Task.CompletedTask;
        }
    }

    private void OnPresentationChanged(
        object? sender,
        OverlayPresentationChangedEventArgs presented
    )
    {
        NotificationPresentation? presentation;
        try
        {
            presentation = ProjectPresentation(presented.State, presented.Requester);
        }
        catch
        {
            // Notifications are advisory and must never disrupt dictation state dispatch.
            return;
        }

        bool startWorker;
        lock (_gate)
        {
            if (_disposed || presented.Revision <= _lastPresentationRevision)
            {
                return;
            }

            _lastPresentationRevision = presented.Revision;
            if (Equals(_desiredPresentation, presentation))
            {
                return;
            }

            _desiredPresentation = presentation;
            _desiredVersion++;
            startWorker = StartWorkerIfNeededLocked();
        }

        if (startWorker)
        {
            _ = DispatchLoopAsync();
        }
    }

    private NotificationPresentation? ProjectPresentation(
        DictationOverlayState state,
        OverlayRequester? requester
    )
    {
        // The recording title/body describe DICTATION's capture protocol (push-to-talk
        // says "release to insert"). Transform is toggle-only, so its recording states
        // fall through to the status branch, whose text carries the correct protocol.
        if (state.IsRecording && requester == OverlayRequester.Dictation)
        {
            return new NotificationPresentation(
                Loc.Instance["Appearance.NotificationRecordingTitle"],
                BodyFor(_settings.Current.Mode),
                0,
                null
            );
        }

        if (state.ShowFeedback && !string.IsNullOrWhiteSpace(state.FeedbackText))
        {
            var globalExpiry = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                _settings.Current.PreviewBubbleAutoHideMilliseconds
            );
            var expiry = globalExpiry == 0
                ? 0
                : AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                    state.FeedbackDurationMilliseconds ?? globalExpiry
                );
            return expiry <= 0
                ? null
                : new NotificationPresentation(
                    state.FeedbackText,
                    state.ActionResultUrl ?? string.Empty,
                    expiry,
                    state.NotificationIconName
                );
        }

        if (state.IsOverlayVisible && !string.IsNullOrWhiteSpace(state.StatusText))
        {
            return new NotificationPresentation(state.StatusText, string.Empty, 0, null);
        }

        return null;
    }

    private bool StartWorkerIfNeededLocked()
    {
        if (_workerRunning)
        {
            return false;
        }

        if (_desiredPresentation is null && _activeId == 0)
        {
            return false;
        }

        _workerRunning = true;
        _idleCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        return true;
    }

    private async Task DispatchLoopAsync()
    {
        while (true)
        {
            NotificationPresentation? presentation;
            uint replaceId;
            uint version;
            lock (_gate)
            {
                presentation = _desiredPresentation;
                replaceId = _activeId;
                version = _desiredVersion;
            }

            uint? shownId = null;
            if (presentation is null)
            {
                if (replaceId != 0)
                {
                    await CloseByIdAsync(replaceId).ConfigureAwait(false);
                }
            }
            else
            {
                shownId = await ShowAsync(presentation, replaceId).ConfigureAwait(false);
            }

            TaskCompletionSource? completed = null;
            lock (_gate)
            {
                if (presentation is null)
                {
                    _activeId = 0;
                }
                else if (shownId is { } id)
                {
                    _activeId = id;
                }

                if (version == _desiredVersion)
                {
                    _workerRunning = false;
                    completed = _idleCompletion;
                    _idleCompletion = null;
                }
            }

            // ReSharper disable once InvertIf -- last statement in the loop; inverting into a `continue` would obscure the signal-and-stop intent.
            if (completed is not null)
            {
                completed.TrySetResult();
                return;
            }
        }
    }

    private async Task<uint?> ShowAsync(
        NotificationPresentation presentation,
        uint replaceId
    )
    {
        try
        {
            var result = await _runner
                .RunAsync(
                    "gdbus",
                    [
                        "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path",
                        "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                        // Stops GOption parsing: the icon and summary carry plugin-supplied text, and a
                        // leading "-" would otherwise be read as an option and abort the whole call.
                        "--",
                        "TypeWhisper", replaceId.ToString(), presentation.IconName ?? ResolveIconPath(),
                        presentation.Summary, presentation.Body, "[]", // actions
                        "{}", // hints
                        presentation.ExpireTimeout.ToString(),
                    ],
                    timeout: s_callTimeout
                )
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return null;
            }

            // gdbus prints "(uint32 N,)" — anchor on "uint32 " to avoid matching the "32" in the type name.
            var match = NotificationIdRegex().Match(result.StandardOutput);
            return match.Success
                   && uint.TryParse(match.Groups[1].Value, out var id)
                   && id != 0
                ? id
                : null;
        }
        catch
        {
            // Notifications are purely advisory — never let one disrupt dictation.
            return null;
        }
    }

    private async Task CloseByIdAsync(uint id)
    {
        try
        {
            await _runner
                .RunAsync(
                    "gdbus",
                    [
                        "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path",
                        "/org/freedesktop/Notifications", "--method",
                        "org.freedesktop.Notifications.CloseNotification", id.ToString(),
                    ],
                    timeout: s_callTimeout
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

    private sealed class CoordinatorPresentationSource(OverlayCoordinator overlayCoordinator)
        : IRecordingNotificationStateSource
    {
        public event EventHandler<OverlayPresentationChangedEventArgs>? PresentationChanged
        {
            add => overlayCoordinator.PresentationChanged += value;
            remove => overlayCoordinator.PresentationChanged -= value;
        }
    }

    private sealed record NotificationPresentation(
        string Summary,
        string Body,
        int ExpireTimeout,
        string? IconName
    );
}
