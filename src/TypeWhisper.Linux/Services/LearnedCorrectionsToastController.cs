using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Surfaces silently-learned target-app corrections as a dedicated toast window on desktop
///     environments (GNOME/KDE/Cinnamon), placed beside the corrected element. Reuses
///     <see cref="LearnedCorrectionsFeedbackPresenter" /> for the pending-batch / 8s-2s timing /
///     undo logic and mirrors its FeedbackChanged stream onto a
///     <see cref="LearnedCorrectionToastWindow" />: a non-empty text shows/updates the toast, an
///     empty text hides it.
///     <para>
///         Inert on tiling WMs, which use <see cref="LearnedCorrectionsNotificationService" />
///         instead — exactly one of the two owns the event so the feedback is never double-shown.
///         The presenter is not thread-safe, so every access (the learned event on a background
///         commit task, the presenter's own auto-hide timer, the Undo click) is marshalled onto
///         Dispatcher.UIThread, matching the notification service's contract.
///     </para>
/// </summary>
public sealed class LearnedCorrectionsToastController : IDisposable
{
    private readonly bool _enabled;
    private readonly TargetAppCorrectionLearningService _learning;
    private readonly LearnedCorrectionsFeedbackPresenter _presenter;
    private readonly LearnedCorrectionToastWindow _window;

    private bool _disposed;

    // Extents of the batch currently being shown, remembered across the presenter's FeedbackChanged
    // events (the learned toast and its undo confirmation) so the window stays anchored to the same
    // element for both. FeedbackChanged carries only text/undo, not the source box.
    private AtSpiScreenRect? _currentExtents;

    public LearnedCorrectionsToastController(
        TargetAppCorrectionLearningService learning,
        IDictionaryService dictionary,
        LearnedCorrectionToastWindow window
    )
    {
        _learning = learning;
        _window = window;

        // Only active on full desktop environments; the notification service covers tiling WMs.
        _enabled = !DesktopDetector.UsesNotificationRecordingIndicator();

        // The presenter's one-shot auto-hide callback re-enters the presenter, so back it with a
        // DispatcherTimer to keep every access on the UI thread (like the overlay VM did).
        _presenter = new LearnedCorrectionsFeedbackPresenter(dictionary, ScheduleUiDelay);
    }

    public void Initialize()
    {
        if (!_enabled)
        {
            return;
        }

        _presenter.FeedbackChanged += OnFeedbackChanged;
        _learning.CorrectionsLearned += OnCorrectionsLearned;
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
        _presenter.FeedbackChanged -= OnFeedbackChanged;
    }

    private void OnCorrectionsLearned(LearnedCorrectionsBatch batch)
    {
        // Fires on a background commit task; marshal onto the UI thread before touching the
        // presenter (single-threaded) or the window. The source extents ride alongside so the
        // toast can be placed beside the corrected element.
        Dispatcher.UIThread.Post(() =>
        {
            _currentExtents = batch.SourceExtents;
            _presenter.ShowLearned(batch.Corrections);
        });
    }

    private void OnFeedbackChanged(LearnedCorrectionsFeedback feedback)
    {
        // Already on the UI thread (ShowLearned/Undo/Hide all run there).
        if (string.IsNullOrEmpty(feedback.Text))
        {
            _window.HideToast();
            return;
        }

        _window.ShowToast(
            feedback.Text,
            feedback.ShowUndo,
            Loc.Instance["Feedback.Undo"],
            _currentExtents,
            OnUndoClicked
        );
    }

    private void OnUndoClicked()
    {
        // The Undo button lives on the UI thread; Undo emits the 2s confirmation via
        // FeedbackChanged, which updates this same toast in place.
        _presenter.Undo();
    }

    // One-shot UI-thread delay for the presenter's auto-hide; disposing the handle cancels the
    // pending callback so a re-arm can't fire the previous timer.
    private static TimerHandle ScheduleUiDelay(TimeSpan delay, Action callback)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            callback();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose()
        {
            timer.Stop();
        }
    }
}
