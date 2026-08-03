using System.Diagnostics;
using Avalonia.Threading;
using Tmds.DBus.Protocol;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Delivers the learned-corrections feedback (with an Undo action) as a desktop
///     notification on tiling WMs, where the dictation overlay — and with it the overlay's
///     feedback band — is suppressed (see
///     <see cref="DesktopDetector.UsesNotificationRecordingIndicator" />). Reuses
///     <see cref="LearnedCorrectionsFeedbackPresenter" /> for the pending-batch/timing/undo
///     logic, and mirrors its FeedbackChanged stream onto an
///     <c>org.freedesktop.Notifications</c> popup: text updates replace the popup in place,
///     an empty text closes it, and the daemon's <c>ActionInvoked</c>/<c>NotificationClosed</c>
///     signals feed back into the presenter.
///     <para>
///         Fully inert on full desktop environments (GNOME/KDE/Cinnamon), which keep the
///         overlay path — no subscriptions and no bus connection. The presenter is not
///         thread-safe, so every access (learned event, D-Bus signal callbacks, timer
///         callbacks) is marshalled onto a single serializing post (Dispatcher.UIThread by
///         default), matching the overlay wiring's contract.
///     </para>
/// </summary>
public sealed class LearnedCorrectionsNotificationService : IDisposable
{
    // Only a daemon-side backstop: the presenter auto-hides (8s learned / 2s confirmation) and
    // closes the popup itself. Finite and well past the 8s window — unlike -1 ("daemon default",
    // which can be shorter and cut Undo short) or 0 ("never expires", which strands a popup we fail
    // to close, e.g. on shutdown mid-show or a failed replacement).
    private const int ServerBackstopExpiryMs = 30_000;

    // gdbus/notify uses id 0 to mean "new notification" for replaces_id; the first Notify
    // passes 0, later ones pass the previous id so the popup is replaced in place.
    private const uint NoReplaceId = 0;

    private readonly INotificationChannel _channel;
    private readonly bool _enabled;
    private readonly IErrorLogService _errorLog;
    private readonly TargetAppCorrectionLearningService _learning;

    // Serializes all presenter access. Defaults to Dispatcher.UIThread.Post (same thread the
    // overlay path marshals to); injectable so tests drive it synchronously without a
    // headless dispatcher.
    private readonly Action<Action> _post;

    private readonly LearnedCorrectionsFeedbackPresenter _presenter;

    private bool _disposed;
    private bool _loggedFailure;

    // Id of the popup currently showing our feedback, 0 when none is up. Only touched via
    // _post, so no lock is needed. Passed as replaces_id so a follow-up (e.g. the undo
    // confirmation) replaces it rather than stacking a second popup.
    private uint _currentId;

    // Single-flight dispatch state, only touched via _post. The D-Bus show/close is async, so two
    // feedback events before the first Notify returns would both read replaces_id 0 and stack a
    // duplicate popup. Instead one op is in flight at a time; a newer event overwrites
    // _pendingFeedback (latest wins) and the in-flight op picks it up on completion.
    private LearnedCorrectionsFeedback? _pendingFeedback;
    private bool _dispatching;

    public LearnedCorrectionsNotificationService(
        TargetAppCorrectionLearningService learning,
        IDictionaryService dictionary,
        IErrorLogService errorLog
    )
        : this(learning, dictionary, errorLog, channel: null, post: null, scheduleDelay: null)
    {
    }

    // Test seam: inject a fake channel (the D-Bus transport is not unit-testable), a
    // synchronous post, and a manually-fired delay so the orchestration (including the
    // presenter's auto-hide → close) can be exercised without a bus, dispatcher, or real timer.
    internal LearnedCorrectionsNotificationService(
        TargetAppCorrectionLearningService learning,
        IDictionaryService dictionary,
        IErrorLogService errorLog,
        INotificationChannel? channel,
        Action<Action>? post,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay
    )
    {
        _learning = learning;
        _errorLog = errorLog;
        _enabled = DesktopDetector.UsesNotificationRecordingIndicator();
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _channel = channel ?? new DBusNotificationChannel();

        // The presenter's auto-hide is a one-shot delay whose callback re-enters the presenter,
        // so it must marshal back through _post like every other presenter access. Production
        // wraps a System.Threading.Timer; tests inject a hand-fired scheduler.
        var schedule = scheduleDelay
            ?? ((delay, callback) => new PostingTimer(delay, () => _post(callback)));
        _presenter = new LearnedCorrectionsFeedbackPresenter(dictionary, errorLog, schedule);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_enabled)
        {
            return;
        }

        _learning.CorrectionsLearned -= OnCorrectionsLearned;
        _channel.ActionInvoked -= OnActionInvoked;
        _channel.Closed -= OnNotificationClosed;
        _presenter.FeedbackChanged -= OnFeedbackChanged;

        // freedesktop notifications outlive their sender's bus connection, so a toast still on
        // screen at shutdown would sit with a now-dead Undo button until the backstop expiry. Close
        // the owned id now so it goes immediately. Best-effort and bounded: a hung daemon must not
        // stall exit; a show still in flight here is caught by the backstop expiry, not this close.
        var liveId = _currentId;
        if (liveId != NoReplaceId)
        {
            try
            {
                _channel.CloseAsync(liveId).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[LearnedCorrectionsNotification] close on dispose failed: {ex.Message}"
                );
            }
        }

        _channel.Dispose();
    }

    public void Initialize()
    {
        if (!_enabled)
        {
            return;
        }

        _presenter.FeedbackChanged += OnFeedbackChanged;
        _channel.ActionInvoked += OnActionInvoked;
        _channel.Closed += OnNotificationClosed;
        _learning.CorrectionsLearned += OnCorrectionsLearned;
    }

    private void OnCorrectionsLearned(LearnedCorrectionsBatch batch)
    {
        // Fires on a background commit task; marshal onto the serializing post before the
        // presenter (which then raises FeedbackChanged synchronously on the same thread). The
        // notification popup has no on-screen anchor, so SourceExtents is ignored here.
        _post(() => _presenter.ShowLearned(batch.Corrections));
    }

    private void OnFeedbackChanged(LearnedCorrectionsFeedback feedback)
    {
        // Already on the serializing post (ShowLearned/Undo/Hide all run there). Record this as the
        // latest desired state; an in-flight show/close will pick it up when it finishes.
        _pendingFeedback = feedback;
        if (!_dispatching)
        {
            DispatchPending();
        }
    }

    // Starts the next show/close if one is queued, else parks. Runs only on the _post thread, so
    // the _currentId read/write here is serialized with the signal callbacks that also read it.
    private void DispatchPending()
    {
        if (_pendingFeedback is not { } feedback)
        {
            _dispatching = false;
            return;
        }

        _pendingFeedback = null;
        _dispatching = true;

        if (string.IsNullOrEmpty(feedback.Text))
        {
            var id = _currentId;
            _currentId = NoReplaceId;
            _ = RunCloseAsync(id);
        }
        else
        {
            _ = RunShowAsync(feedback, _currentId);
        }
    }

    private async Task RunShowAsync(LearnedCorrectionsFeedback feedback, uint replacesId)
    {
        uint? shownId = null;
        try
        {
            shownId = await _channel
                .ShowAsync(replacesId, feedback.Text, feedback.ShowUndo)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Feedback is best-effort; a failed notification must never affect learning. Log
            // once (the notification daemon is likely missing/unreachable — a persistent
            // condition, not worth a line per learned batch); everything after is Trace-only.
            Trace.WriteLine($"[LearnedCorrectionsNotification] show failed: {ex.Message}");
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _errorLog.AddEntry(
                    $"Learned-corrections notification failed: {ex.Message}",
                    ErrorCategory.Detection
                );
            }
        }

        // Record the id and pump the next queued feedback on the post thread so both stay
        // serialized with the reads and with each other.
        _post(() =>
        {
            if (shownId is { } id)
            {
                _currentId = id;
            }
            else if (replacesId != NoReplaceId)
            {
                // A replacement Notify failed: the previous popup (replacesId) may still be on
                // screen while the presenter has advanced to a newer batch, so its stale Undo would
                // act on the wrong batch. Drop our claim on it and close the leftover; the current
                // batch is shown by the next dispatch (or cleared by its own auto-hide).
                _currentId = NoReplaceId;
                _ = CloseOrphanAsync(replacesId);
            }

            DispatchPending();
        });
    }

    // Closes a popup OUTSIDE the single-flight dispatch (so it can't re-enter DispatchPending and
    // break the one-in-flight invariant). Best-effort: clears a leftover whose replacement failed.
    private async Task CloseOrphanAsync(uint id)
    {
        try
        {
            await _channel.CloseAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LearnedCorrectionsNotification] orphan close failed: {ex.Message}");
        }
    }

    private async Task RunCloseAsync(uint id)
    {
        if (id != NoReplaceId)
        {
            try
            {
                await _channel.CloseAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LearnedCorrectionsNotification] close failed: {ex.Message}");
            }
        }

        _post(DispatchPending);
    }

    private void OnActionInvoked(uint id, string actionKey)
    {
        _post(() =>
        {
            // Ignore actions on notifications that aren't the one we currently own (a stale
            // popup, or one already superseded by a newer batch).
            if (id != _currentId || !string.Equals(actionKey, "undo", StringComparison.Ordinal))
            {
                return;
            }

            // A show/close is in flight, so a newer batch may already have replaced the presenter's
            // pending batch while this popup still displays (and is keyed to) the old one. Undoing
            // now would delete the newer batch instead — ignore until the display settles; the
            // replacement popup carries its own live Undo.
            if (_dispatching)
            {
                return;
            }

            // Undo emits the 2s confirmation via FeedbackChanged, which replaces this popup
            // in place (same _currentId as replaces_id).
            _presenter.Undo();
        });
    }

    private void OnNotificationClosed(uint id, uint reason)
    {
        _post(() =>
        {
            if (id != _currentId)
            {
                return;
            }

            // The user (or daemon) dismissed the popup; drop the batch so a later
            // ActionInvoked can't act on an invisible, un-undoable toast. Reset is silent, so
            // no FeedbackChanged fires to re-show it.
            _currentId = NoReplaceId;
            _presenter.Reset();
        });
    }

    /// <summary>
    ///     Notification transport behind which the D-Bus implementation is hidden so the
    ///     orchestration is unit-testable with a fake. <see cref="ActionInvoked" /> and
    ///     <see cref="Closed" /> carry the freedesktop signal args (notification id + key /
    ///     reason).
    /// </summary>
    internal interface INotificationChannel : IDisposable
    {
        event Action<uint, string>? ActionInvoked;
        event Action<uint, uint>? Closed;

        /// <summary>
        ///     Shows or replaces a notification and returns its id. When
        ///     <paramref name="replacesId" /> is 0 a new popup is created; otherwise the
        ///     existing one is updated in place.
        /// </summary>
        Task<uint> ShowAsync(uint replacesId, string summary, bool withUndoAction);

        Task CloseAsync(uint id);
    }

    // System.Threading.Timer wrapped as the presenter's one-shot cancel handle. Disposal
    // cancels the pending callback (the presenter re-arms by disposing the prior handle).
    private sealed class PostingTimer : IDisposable
    {
        private readonly Timer _timer;

        public PostingTimer(TimeSpan delay, Action callback)
        {
            _timer = new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }

    /// <summary>
    ///     <see cref="INotificationChannel" /> over the session bus using
    ///     <c>Tmds.DBus.Protocol</c> directly (not gdbus): a subprocess can't receive the
    ///     daemon's ActionInvoked/NotificationClosed signals, which the Undo button needs.
    ///     Connects lazily on the first show; every one-shot call is time-bounded so a hung
    ///     daemon can't pin the task.
    /// </summary>
    private sealed class DBusNotificationChannel : INotificationChannel
    {
        private const string NotificationsService = "org.freedesktop.Notifications";
        private const string NotificationsPath = "/org/freedesktop/Notifications";
        private const string NotificationsInterface = "org.freedesktop.Notifications";

        // Bound one-shot Notify/CloseNotification calls; a hung daemon must not pin us. Same
        // rationale as AtSpiEventClient's call timeout (Tmds applies none of its own).
        private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(4);

        private static readonly MessageValueReader<uint> s_readUInt32 =
            static (m, _) => m.GetBodyReader().ReadUInt32();

        // ActionInvoked body is (u id, s action_key).
        private static readonly MessageValueReader<(uint Id, string Action)> s_readActionInvoked =
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var id = reader.ReadUInt32();
                var action = reader.ReadString();
                return (id, action);
            };

        // NotificationClosed body is (u id, u reason).
        private static readonly MessageValueReader<(uint Id, uint Reason)> s_readClosed =
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var id = reader.ReadUInt32();
                var reason = reader.ReadUInt32();
                return (id, reason);
            };

        private readonly SemaphoreSlim _connectGate = new(1, 1);

        private DBusConnection? _connection;
        private bool _disposed;
        private IDisposable? _actionSubscription;
        private IDisposable? _closedSubscription;

        // 1 once a signal observer reported a fatal connection error and a reset was scheduled;
        // back to 0 when a fresh connection is established. One reset per dead connection.
        private int _resetScheduled;

        public event Action<uint, string>? ActionInvoked;
        public event Action<uint, uint>? Closed;

        public async Task<uint> ShowAsync(uint replacesId, string summary, bool withUndoAction)
        {
            var conn = await EnsureConnectedAsync().ConfigureAwait(false);

            MessageBuffer message;
            using (var writer = conn.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: NotificationsService,
                    path: NotificationsPath,
                    @interface: NotificationsInterface,
                    member: "Notify",
                    signature: "susssasa{sv}i"
                );
                writer.WriteString("TypeWhisper"); // app_name
                writer.WriteUInt32(replacesId); // replaces_id
                writer.WriteString(string.Empty); // app_icon: this feedback toast carries no icon
                writer.WriteString(summary); // summary
                writer.WriteString(string.Empty); // body

                // actions: ["undo", <label>] pairs the action key with its display label.
                var actions = writer.WriteArrayStart(DBusType.String);
                if (withUndoAction)
                {
                    writer.WriteString("undo");
                    writer.WriteString(Loc.Instance["Feedback.Undo"]);
                }

                writer.WriteArrayEnd(actions);

                // hints: empty a{sv}.
                var hints = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(hints);

                writer.WriteInt32(ServerBackstopExpiryMs); // expire_timeout: presenter closes it first; this only backstops leftovers
                message = writer.CreateMessage();
            }

            return await conn.CallMethodAsync(message, s_readUInt32)
                .WaitAsync(s_callTimeout)
                .ConfigureAwait(false);
        }

        public async Task CloseAsync(uint id)
        {
            var conn = _connection;
            if (conn is null)
            {
                return;
            }

            MessageBuffer message;
            using (var writer = conn.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: NotificationsService,
                    path: NotificationsPath,
                    @interface: NotificationsInterface,
                    member: "CloseNotification",
                    signature: "u"
                );
                writer.WriteUInt32(id);
                message = writer.CreateMessage();
            }

            await conn.CallMethodAsync(message).WaitAsync(s_callTimeout).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _actionSubscription?.Dispose();
                _closedSubscription?.Dispose();
                _connection?.Dispose();
            }
            catch
            {
                // best effort — teardown of a dying bus connection must not throw.
            }

            _connectGate.Dispose();
        }

        private async Task<DBusConnection> EnsureConnectedAsync()
        {
            var existing = _connection;
            if (existing is not null)
            {
                return existing;
            }

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_connection is not null)
                {
                    return _connection;
                }

                // A dedicated session connection (not DBusConnection.Session) so our signal
                // subscriptions and their lifetime are wholly ours to dispose.
                var address = DBusAddress.Session
                    ?? throw new InvalidOperationException("No session bus address available.");
                var conn = new DBusConnection(address);
                try
                {
                    await conn.ConnectAsync().ConfigureAwait(false);

                    _actionSubscription = await conn.AddMatchAsync(
                        new MatchRule
                        {
                            Type = MessageType.Signal,
                            Sender = NotificationsService,
                            Interface = NotificationsInterface,
                            Path = NotificationsPath,
                            Member = "ActionInvoked",
                        },
                        s_readActionInvoked,
                        HandleActionInvoked,
                        ObserverFlags.None,
                        emitOnCapturedContext: false
                    ).ConfigureAwait(false);

                    _closedSubscription = await conn.AddMatchAsync(
                        new MatchRule
                        {
                            Type = MessageType.Signal,
                            Sender = NotificationsService,
                            Interface = NotificationsInterface,
                            Path = NotificationsPath,
                            Member = "NotificationClosed",
                        },
                        s_readClosed,
                        HandleClosed,
                        ObserverFlags.None,
                        emitOnCapturedContext: false
                    ).ConfigureAwait(false);

                    _connection = conn;
                    // Fresh connection: re-arm the one-reset-per-connection guard so a later
                    // disconnect of THIS connection schedules its own reset.
                    Interlocked.Exchange(ref _resetScheduled, 0);
                    return conn;
                }
                catch
                {
                    // ConnectAsync or a match subscription failed partway. Tear down the partial
                    // state so a persistent session-bus failure can't leak a socket or a dangling
                    // subscription across the retry every later learned batch triggers. Mirrors the
                    // AT-SPI startup path's cleanup.
                    try
                    {
                        _actionSubscription?.Dispose();
                        _closedSubscription?.Dispose();
                        conn.Dispose();
                    }
                    catch
                    {
                        // best effort — teardown of a half-open connection must not throw.
                    }

                    _actionSubscription = null;
                    _closedSubscription = null;
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private void HandleActionInvoked(
            Exception? exception,
            (uint Id, string Action) signal,
            object? readerState,
            object? handlerState
        )
        {
            // On error/disconnect the observer is invoked with a non-null exception and a default
            // value. Reset so the next show reconnects rather than reusing the dead connection.
            if (exception is not null)
            {
                ScheduleReset();
                return;
            }

            ActionInvoked?.Invoke(signal.Id, signal.Action);
        }

        private void HandleClosed(
            Exception? exception,
            (uint Id, uint Reason) signal,
            object? readerState,
            object? handlerState
        )
        {
            if (exception is not null)
            {
                ScheduleReset();
                return;
            }

            Closed?.Invoke(signal.Id, signal.Reason);
        }

        // A signal observer reported a fatal connection error (session bus gone/restarted).
        // Without a reset EnsureConnectedAsync keeps handing back the dead connection, so every
        // later show fails until process restart. Tear it down once (off the reader thread) so the
        // next show reconnects. Mirrors AtSpiEventClient.OnObserverError.
        private void ScheduleReset()
        {
            if (_disposed || Interlocked.Exchange(ref _resetScheduled, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _connectGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        _actionSubscription?.Dispose();
                        _closedSubscription?.Dispose();
                        _connection?.Dispose();
                    }
                    finally
                    {
                        _actionSubscription = null;
                        _closedSubscription = null;
                        _connection = null;
                        _connectGate.Release();
                    }
                }
                catch
                {
                    // Best effort: resetting a dead connection (or racing disposal) must not throw.
                }
            });
        }
    }
}
