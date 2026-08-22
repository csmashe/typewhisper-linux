using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

public enum OverlayRequester
{
    Dictation,
    Transform,

    /// <summary>
    ///     Ancillary system toasts (e.g. detection-failure feedback) that never own workflow
    ///     UI; their feedback always ranks transient and waits behind active work.
    /// </summary>
    System,
}

public enum OverlayPriority
{
    None,
    TransientFeedback,
    TerminalFeedback,
    Processing,
    ActiveRecording,
}

public sealed class OverlayPresentationToken
{
    internal OverlayPresentationToken(OverlayRequester requester)
    {
        Requester = requester;
    }

    public OverlayRequester Requester { get; }
}

public sealed class OverlayPresentationChangedEventArgs(
    long revision,
    DictationOverlayState state,
    OverlayRequester? requester
) : EventArgs
{
    public long Revision { get; } = revision;
    public DictationOverlayState State { get; } = state;

    /// <summary>The workflow whose claim won arbitration; null when nothing is presented.</summary>
    public OverlayRequester? Requester { get; } = requester;
}

/// <summary>
///     Arbitrates the single overlay surface between dictation and transform workflows.
///     A requester owns at most one live claim; acquiring a replacement invalidates its prior
///     token, while claims from the other requester compete by priority and claim age.
/// </summary>
public sealed class OverlayCoordinator
{
    private readonly Dictionary<OverlayRequester, Slot> _slots = [];
    private readonly Lock _sync = new();
    private readonly ISettingsService _settings;
    private readonly Action<Action> _postToUiThread;
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleDelay;

    private long _claimGeneration;
    private OverlayPresentationToken? _presentedToken;
    private DictationOverlayState _presentedState = DictationOverlayState.Hidden;
    private long _revision;

    public OverlayCoordinator(ISettingsService settings)
        : this(
            settings,
            static action => Dispatcher.UIThread.Post(action),
            static (delay, callback) => new OneShotTimer(delay, callback)
        )
    {
    }

    // Test seam: production posts every presentation through Avalonia's UI dispatcher and uses a
    // one-shot timer; tests can capture/reorder posts and manually fire feedback expiry.
    internal OverlayCoordinator(
        ISettingsService settings,
        Action<Action> postToUiThread,
        Func<TimeSpan, Action, IDisposable>? scheduleDelay = null
    )
    {
        _settings = settings;
        _postToUiThread = postToUiThread;
        _scheduleDelay = scheduleDelay
                         ?? (static (delay, callback) => new OneShotTimer(delay, callback));
    }

    public event EventHandler<OverlayPresentationChangedEventArgs>? PresentationChanged;

    public OverlayPresentationToken Acquire(OverlayRequester requester)
    {
        Presentation? presentation;
        OverlayPresentationToken token;
        lock (_sync)
        {
            if (_slots.Remove(requester, out var previous))
            {
                CancelExpiryLocked(previous);
            }

            token = new OverlayPresentationToken(requester);
            _slots.Add(requester, new Slot(token, ++_claimGeneration));
            presentation = ReevaluateLocked();
        }

        Dispatch(presentation);
        return token;
    }

    public bool Show(OverlayPresentationToken token, DictationOverlayState state)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(state);

        var globalAutoHideMilliseconds = ReadGlobalAutoHideMilliseconds();
        Presentation? presentation;
        lock (_sync)
        {
            if (!TryGetSlotLocked(token, out var slot))
            {
                return false;
            }

            SetStateLocked(slot, state, globalAutoHideMilliseconds);
            presentation = ReevaluateLocked(token);
        }

        Dispatch(presentation);
        return true;
    }

    // The updater runs under the coordinator lock: it must be pure — no coordinator
    // calls, no other locks, no blocking work.
    public bool Update(
        OverlayPresentationToken token,
        Func<DictationOverlayState, DictationOverlayState> updater
    )
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(updater);

        var globalAutoHideMilliseconds = ReadGlobalAutoHideMilliseconds();
        Presentation? presentation;
        lock (_sync)
        {
            if (!TryGetSlotLocked(token, out var slot))
            {
                return false;
            }

            var state = updater(slot.State)
                        ?? throw new InvalidOperationException(
                            "An overlay state updater returned null."
                        );
            SetStateLocked(slot, state, globalAutoHideMilliseconds);
            presentation = ReevaluateLocked(token);
        }

        Dispatch(presentation);
        return true;
    }

    /// <summary>
    ///     Replaces the currently presented feedback in place, keeping the presenting claim's
    ///     rank and age while re-arming expiry from the replacement state. Lets ancillary UI
    ///     (e.g. the action-result command) swap feedback it holds no token for without leaving
    ///     the sole-render-path. Returns false when the current presentation is not feedback.
    /// </summary>
    // The updater runs under the coordinator lock: it must be pure — no coordinator
    // calls, no other locks, no blocking work.
    public bool ReplacePresentedFeedback(
        Func<DictationOverlayState, DictationOverlayState> updater
    )
    {
        ArgumentNullException.ThrowIfNull(updater);

        var globalAutoHideMilliseconds = ReadGlobalAutoHideMilliseconds();
        Presentation? presentation;
        lock (_sync)
        {
            if (_presentedToken is not { } token
                || !TryGetSlotLocked(token, out var slot)
                || slot.Priority is not (
                    OverlayPriority.TerminalFeedback or OverlayPriority.TransientFeedback
                ))
            {
                return false;
            }

            var state = updater(slot.State)
                        ?? throw new InvalidOperationException(
                            "An overlay state updater returned null."
                        );
            SetStateLocked(slot, state, globalAutoHideMilliseconds);
            presentation = ReevaluateLocked(token);
        }

        Dispatch(presentation);
        return true;
    }

    public bool Hide(OverlayPresentationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        Presentation? presentation;
        lock (_sync)
        {
            if (!TryGetSlotLocked(token, out var slot))
            {
                return false;
            }

            // Hidden never arms feedback expiry, so no settings pre-read is needed here.
            SetStateLocked(slot, DictationOverlayState.Hidden, globalAutoHideMilliseconds: 0);
            presentation = ReevaluateLocked(token);
        }

        Dispatch(presentation);
        return true;
    }

    public bool Release(OverlayPresentationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        Presentation? presentation;
        lock (_sync)
        {
            if (!TryGetSlotLocked(token, out var slot))
            {
                return false;
            }

            CancelExpiryLocked(slot);
            _slots.Remove(token.Requester);
            presentation = ReevaluateLocked();
        }

        Dispatch(presentation);
        return true;
    }

    internal DictationOverlayState PresentedState
    {
        get
        {
            lock (_sync)
            {
                return _presentedState;
            }
        }
    }

    internal long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    private bool TryGetSlotLocked(OverlayPresentationToken token, out Slot slot)
    {
        if (_slots.TryGetValue(token.Requester, out var candidate)
            && ReferenceEquals(candidate.Token, token))
        {
            slot = candidate;
            return true;
        }

        slot = null!;
        return false;
    }

    private void SetStateLocked(Slot slot, DictationOverlayState state, int globalAutoHideMilliseconds)
    {
        CancelExpiryLocked(slot);
        slot.State = state;
        slot.Priority = DerivePriority(slot, state);
        if (slot.Priority is OverlayPriority.ActiveRecording or OverlayPriority.Processing)
        {
            slot.HasOwnedWorkflow = true;
        }
        else if (slot.Priority == OverlayPriority.None)
        {
            // Returning to hidden ends the token's workflow: later feedback through the
            // same token is a fresh toast, not the workflow's terminal outcome.
            slot.HasOwnedWorkflow = false;
        }

        if (slot.Priority is not (
                OverlayPriority.TerminalFeedback or OverlayPriority.TransientFeedback
            ))
        {
            return;
        }

        var milliseconds = FeedbackExpiryMilliseconds(state, globalAutoHideMilliseconds);
        if (milliseconds <= 0)
        {
            RetireSlotLocked(slot);
            return;
        }

        var expiryGeneration = slot.ExpiryGeneration;
        slot.Expiry = _scheduleDelay(
            TimeSpan.FromMilliseconds(milliseconds),
            () => ExpireFeedback(slot.Token, expiryGeneration)
        );
    }

    // _settings.Current can lazily reload from disk (and may throw); reading it inside
    // _sync would stall every overlay arbitration on I/O, and a throw mid-publish would
    // strand the slot at feedback priority with no expiry armed. Callers pre-read before
    // taking the lock; a failed read falls back to the normalized default.
    private int ReadGlobalAutoHideMilliseconds()
    {
        try
        {
            return AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                _settings.Current.PreviewBubbleAutoHideMilliseconds
            );
        }
        catch
        {
            return AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                AppSettings.DefaultPreviewBubbleAutoHideMilliseconds
            );
        }
    }

    private static int FeedbackExpiryMilliseconds(
        DictationOverlayState state,
        int globalMilliseconds
    )
    {
        return globalMilliseconds == 0
            ? 0
            : AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                state.FeedbackDurationMilliseconds ?? globalMilliseconds
            );
    }

    private static OverlayPriority DerivePriority(Slot slot, DictationOverlayState state)
    {
        if (state.IsRecording)
        {
            return OverlayPriority.ActiveRecording;
        }

        if (state.ShowFeedback)
        {
            // A feedback transition from a token that owned workflow UI is its terminal outcome.
            // Feedback claimed directly is ancillary/transient and may wait behind active work.
            return slot.HasOwnedWorkflow
                ? OverlayPriority.TerminalFeedback
                : OverlayPriority.TransientFeedback;
        }

        return state.IsOverlayVisible
            ? OverlayPriority.Processing
            : OverlayPriority.None;
    }

    private Presentation? ReevaluateLocked(OverlayPresentationToken? forceToken = null)
    {
        var winner = SelectWinnerLocked();

        // Once terminal feedback loses arbitration, its moment has passed. Clearing the slot now
        // prevents an old completion from resurfacing after the winning workflow ends.
        foreach (var slot in _slots.Values)
        {
            if (slot.Priority == OverlayPriority.TerminalFeedback
                && !ReferenceEquals(slot, winner))
            {
                RetireSlotLocked(slot);
            }
        }

        winner = SelectWinnerLocked();
        var token = winner?.Token;
        var state = winner?.State ?? DictationOverlayState.Hidden;
        var force = forceToken is not null && ReferenceEquals(token, forceToken);
        if (!force
            && ReferenceEquals(token, _presentedToken)
            && state == _presentedState)
        {
            return null;
        }

        _presentedToken = token;
        _presentedState = state;
        return new Presentation(++_revision, state, token?.Requester);
    }

    private Slot? SelectWinnerLocked()
    {
        Slot? winner = null;
        foreach (var slot in _slots.Values)
        {
            if (slot.Priority == OverlayPriority.None)
            {
                continue;
            }

            if (winner is null
                || slot.Priority > winner.Priority
                || (slot.Priority == winner.Priority
                    && slot.ClaimGeneration < winner.ClaimGeneration))
            {
                winner = slot;
            }
        }

        return winner;
    }

    private void CancelExpiryLocked(Slot slot)
    {
        slot.ExpiryGeneration++;
        slot.Expiry?.Dispose();
        slot.Expiry = null;
    }

    // A retired slot's next feedback is a fresh toast, so workflow ownership resets too.
    private void RetireSlotLocked(Slot slot)
    {
        CancelExpiryLocked(slot);
        slot.State = DictationOverlayState.Hidden;
        slot.Priority = OverlayPriority.None;
        slot.HasOwnedWorkflow = false;
    }

    private void ExpireFeedback(OverlayPresentationToken token, long expiryGeneration)
    {
        Presentation? presentation;
        lock (_sync)
        {
            if (!TryGetSlotLocked(token, out var slot)
                || slot.ExpiryGeneration != expiryGeneration
                || slot.Priority is not (
                    OverlayPriority.TerminalFeedback or OverlayPriority.TransientFeedback
                ))
            {
                return;
            }

            RetireSlotLocked(slot);
            presentation = ReevaluateLocked();
        }

        Dispatch(presentation);
    }

    private void Dispatch(Presentation? presentation)
    {
        if (presentation is not { } value)
        {
            return;
        }

        _postToUiThread(() =>
            PresentationChanged?.Invoke(
                this,
                new OverlayPresentationChangedEventArgs(value.Revision, value.State, value.Requester)
            )
        );
    }

    private sealed class Slot(OverlayPresentationToken token, long claimGeneration)
    {
        public OverlayPresentationToken Token { get; } = token;
        public long ClaimGeneration { get; } = claimGeneration;
        public DictationOverlayState State { get; set; } = DictationOverlayState.Hidden;
        public OverlayPriority Priority { get; set; }
        public bool HasOwnedWorkflow { get; set; }
        public long ExpiryGeneration { get; set; }
        public IDisposable? Expiry { get; set; }
    }

    private readonly record struct Presentation(
        long Revision,
        DictationOverlayState State,
        OverlayRequester? Requester
    );

    private sealed class OneShotTimer : IDisposable
    {
        private readonly Timer _timer;

        public OneShotTimer(TimeSpan delay, Action callback)
        {
            _timer = new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
