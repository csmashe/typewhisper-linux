using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Immutable snapshot of the learned-corrections feedback the overlay should render:
///     the toast text plus whether the Undo affordance is live. Empty <see cref="Text" />
///     means "hide the toast".
/// </summary>
public sealed record LearnedCorrectionsFeedback(string Text, bool ShowUndo)
{
    public static LearnedCorrectionsFeedback Hidden { get; } = new(string.Empty, false);
}

/// <summary>
///     UI-thread-agnostic presenter for the Wispr-Flow-style "Learned 'X' → 'Y'" toast with
///     an Undo action, mirroring the Windows app's ShowLearnedCorrectionsFeedback flow. Holds
///     the pending batch and all the timing/undo logic so the overlay view model stays a thin
///     binding surface. Timing is injected via a one-shot <c>scheduleDelay</c> factory
///     so this is exercisable headless (production wires a DispatcherTimer; tests fire manually).
///     <para>
///         All members must be touched on the UI thread — the composition-root subscription to
///         <c>CorrectionsLearned</c> marshals onto it before calling <see cref="ShowLearned" />.
///     </para>
/// </summary>
public sealed class LearnedCorrectionsFeedbackPresenter
{
    // Matches the Windows app: a learned-corrections toast lingers for 8s so the user can read
    // it and decide to undo; the post-undo confirmation is a brief 2s acknowledgement.
    private static readonly TimeSpan s_learnedAutoHide = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan s_confirmationAutoHide = TimeSpan.FromSeconds(2);

    private readonly IDictionaryService _dictionary;
    private readonly IErrorLogService _errorLog;

    // Schedules a one-shot delay and returns a handle whose disposal cancels the pending
    // callback. Re-arming disposes the previous handle so only the latest timer can fire.
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleDelay;

    private IDisposable? _autoHide;
    private List<LearnedDictionaryCorrection> _pending = [];

    // Bumped on every Emit/Reset so a superseded auto-hide no-ops. The production scheduler wraps a
    // System.Threading.Timer that posts Hide to the UI thread; if that callback is already queued
    // when a newer ShowLearned/Undo re-arms, disposing the old timer can't retract it, and the
    // stale Hide would otherwise clear the fresh pending batch and close its Undo toast at once.
    private int _feedbackGeneration;

    public LearnedCorrectionsFeedbackPresenter(
        IDictionaryService dictionary,
        IErrorLogService errorLog,
        Func<TimeSpan, Action, IDisposable> scheduleDelay
    )
    {
        _dictionary = dictionary;
        _errorLog = errorLog;
        _scheduleDelay = scheduleDelay;
    }

    /// <summary>
    ///     Raised whenever the toast should change: the overlay view model pushes
    ///     <see cref="LearnedCorrectionsFeedback.Text" /> and Undo visibility into its bindings.
    /// </summary>
    public event Action<LearnedCorrectionsFeedback>? FeedbackChanged;

    /// <summary>True while a batch is pending undo (Undo is live only in this window).</summary>
    public bool HasPendingBatch => _pending.Count > 0;

    /// <summary>
    ///     Surfaces a freshly learned batch. A new learn while a previous toast is still up
    ///     replaces the pending batch (matches the Windows behavior) and re-arms the 8s hide.
    /// </summary>
    public void ShowLearned(IReadOnlyList<LearnedDictionaryCorrection> learned)
    {
        if (learned.Count == 0)
        {
            return;
        }

        _pending = [.. learned];

        var text = learned.Count == 1
            ? Loc.Instance.GetString(
                "Feedback.LearnedCorrectionFormat",
                learned[0].Original,
                learned[0].Replacement)
            : Loc.Instance.GetString("Feedback.LearnedCorrectionsFormat", learned.Count);

        Emit(new LearnedCorrectionsFeedback(text, ShowUndo: true), s_learnedAutoHide);
    }

    /// <summary>
    ///     Removes the pending batch from the dictionary and swaps the toast for a brief
    ///     confirmation. No-ops when nothing is pending (e.g. a double click after auto-hide).
    /// </summary>
    public void Undo()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        try
        {
            _dictionary.UndoLearnedCorrections(_pending);
        }
        catch (Exception ex)
        {
            _errorLog.AddEntry(
                $"Failed to undo learned correction: {ex.Message}");
            Emit(
                new LearnedCorrectionsFeedback(
                    Loc.Instance["Feedback.CorrectionUndoFailed"],
                    ShowUndo: true),
                s_learnedAutoHide);
            return;
        }

        _pending = [];

        Emit(
            new LearnedCorrectionsFeedback(
                Loc.Instance["Feedback.CorrectionLearningUndone"],
                ShowUndo: false),
            s_confirmationAutoHide);
    }

    /// <summary>
    ///     Silently drops the pending batch and cancels the auto-hide without raising
    ///     <see cref="FeedbackChanged" />. Used when something else (a new dictation) has
    ///     taken over the feedback band, so the toast doesn't reassert itself or fire a
    ///     stale hide over the new content.
    /// </summary>
    public void Reset()
    {
        _autoHide?.Dispose();
        _autoHide = null;
        _pending = [];
        // Invalidate any auto-hide already posted before this Reset (see _feedbackGeneration).
        _feedbackGeneration++;
    }

    private void Emit(LearnedCorrectionsFeedback feedback, TimeSpan autoHide)
    {
        _autoHide?.Dispose();
        var generation = ++_feedbackGeneration;
        FeedbackChanged?.Invoke(feedback);
        _autoHide = _scheduleDelay(autoHide, () => Hide(generation));
    }

    private void Hide(int generation)
    {
        // A superseded timer's callback may already be queued when this runs; only the latest
        // generation may hide, so a stale Hide can't clear a fresh batch it doesn't own.
        if (generation != _feedbackGeneration)
        {
            return;
        }

        // Auto-hide clears the pending batch too, so Undo can't act on a toast the user can no
        // longer see (matches the Windows app dropping _pendingLearnedCorrections on expiry).
        _autoHide?.Dispose();
        _autoHide = null;
        _pending = [];
        FeedbackChanged?.Invoke(LearnedCorrectionsFeedback.Hidden);
    }
}
