using SharpHook.Native;
using System.Diagnostics;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Backend-neutral press/release state machine: takes a stream of
///     <c>(KeyCode, ModifierMask, pressed)</c> tuples, matches against the
///     current <see cref="GlobalShortcutSet" />, applies the recording-mode
///     state machine, and raises typed events. Owned by both SharpHook and
///     evdev backends so the user-visible behavior is identical regardless of
///     the event source.
/// </summary>
internal sealed class ShortcutDispatcher
{
    // Hybrid mode: holds shorter than this are treated as Toggle; longer holds
    // behave like Push-to-Talk (key release stops recording). 600 ms was chosen
    // as a deliberate tap vs. press boundary — long enough not to misfire on a
    // quick toggle tap, short enough to feel responsive when held.
    private const int PushToTalkThresholdMs = 600;

    private readonly object _lock = new();
    private bool _cancelKeyDown;
    private bool _copyLastKeyDown;
    private bool _dictationKeyDown;
    private DateTime _dictationKeyDownTime;
    private bool _promptKeyDown;
    // Per-action key-down dedup, keyed by the physical KeyCode that matched
    // at press time. Storing the press-time match means release-time cleanup
    // is independent of the current shortcut set — if the user edits or
    // removes the bound action between press and release, the release of
    // the physical key still clears the entry. Without that, a hold + edit
    // cycle would strand the action ID in the dedup set and silently
    // suppress all future presses of that action.
    private readonly Dictionary<KeyCode, string> _promptActionKeyDown = new();
    private bool _recentKeyDown;

    private GlobalShortcutSet? _shortcuts;
    private bool _transformSelectionKeyDown;

    public event Action? DictationToggleRequested;
    public event Action? DictationStartRequested;
    public event Action? DictationStopRequested;
    public event Action? PromptPaletteRequested;
    public event Action? TransformSelectionRequested;
    public event Action? RecentTranscriptionsRequested;
    public event Action? CopyLastTranscriptionRequested;
    public event Action? CancelRequested;
    public event Action<string>? PromptActionRequested;

    public void UpdateShortcuts(GlobalShortcutSet shortcuts)
    {
        Volatile.Write(ref _shortcuts, shortcuts);
    }

    public void ClearShortcuts()
    {
        Volatile.Write(ref _shortcuts, null);
    }

    /// <summary>
    ///     Drives the state machine from a backend-neutral key event. Returns
    ///     silently if no shortcut set is currently registered.
    /// </summary>
    public void Handle(KeyCode key, ModifierMask mods, bool pressed)
    {
        var set = Volatile.Read(ref _shortcuts);
        if (set is null)
        {
            return;
        }

        if (pressed)
        {
            HandlePress(key, mods, set);
        }
        else
        {
            HandleRelease(key, set);
        }
    }

    private void HandlePress(KeyCode key, ModifierMask mods, GlobalShortcutSet set)
    {
        var match = ShortcutMatcher.Match(key, mods, set, out var promptActionId);

        // Cancel: only fires while a dictation is active and only when it
        // doesn't collide with another binding — otherwise we fall through
        // so the regular matcher handles the press.
        if (match == ShortcutMatchKind.Cancel)
        {
            lock (_lock)
            {
                if (_cancelKeyDown)
                {
                    return;
                }

                _cancelKeyDown = true;
            }

            if (set.IsCancelEnabled && !ShortcutMatcher.CancelCollidesWithAnyBinding(set))
            {
                Raise(CancelRequested, nameof(CancelRequested));
                return;
            }

            // Cancel collides with a configured binding — re-match without
            // cancel so that other binding can fire.
            match = ShortcutMatcher.Match(
                key,
                mods,
                set with { CancelKey = KeyCode.VcUndefined },
                out promptActionId
            );
        }

        switch (match)
        {
            case ShortcutMatchKind.PromptAction:
                if (promptActionId is null)
                {
                    return;
                }

                lock (_lock)
                {
                    if (_promptActionKeyDown.ContainsKey(key))
                    {
                        return;
                    }

                    _promptActionKeyDown[key] = promptActionId;
                }

                RaisePromptAction(promptActionId);
                return;
            case ShortcutMatchKind.RecentTranscriptions:
                if (!TryClaimKeyDown(ref _recentKeyDown))
                {
                    return;
                }

                Raise(RecentTranscriptionsRequested, nameof(RecentTranscriptionsRequested));
                return;

            case ShortcutMatchKind.CopyLastTranscription:
                if (!TryClaimKeyDown(ref _copyLastKeyDown))
                {
                    return;
                }

                Raise(CopyLastTranscriptionRequested, nameof(CopyLastTranscriptionRequested));
                return;

            case ShortcutMatchKind.TransformSelection:
                if (!TryClaimKeyDown(ref _transformSelectionKeyDown))
                {
                    return;
                }

                Raise(TransformSelectionRequested, nameof(TransformSelectionRequested));
                return;

            case ShortcutMatchKind.PromptPalette:
                if (!TryClaimKeyDown(ref _promptKeyDown))
                {
                    return;
                }

                Raise(PromptPaletteRequested, nameof(PromptPaletteRequested));
                return;

            case ShortcutMatchKind.Dictation:
                bool claimed;
                lock (_lock)
                {
                    if (_dictationKeyDown)
                    {
                        return;
                    }

                    _dictationKeyDown = true;
                    _dictationKeyDownTime = DateTime.UtcNow;
                    claimed = true;
                }

                if (!claimed)
                {
                    return;
                }

                switch (set.Mode)
                {
                    case RecordingMode.Toggle:
                        Raise(DictationToggleRequested, nameof(DictationToggleRequested));
                        break;
                    case RecordingMode.PushToTalk:
                        Raise(DictationStartRequested, nameof(DictationStartRequested));
                        break;
                    case RecordingMode.Hybrid:
                        // Always fire Toggle on press. If the key is held past the
                        // threshold, HandleRelease will additionally fire Stop
                        // to end a push-to-talk segment; short taps just toggle.
                        Raise(DictationToggleRequested, nameof(DictationToggleRequested));
                        break;
                }

                return;
        }
    }

    private void HandleRelease(KeyCode key, GlobalShortcutSet set)
    {
        // Clear repeat-guards on the matching key release. Modifier-only
        // releases are ignored — the user can let go of Ctrl/Shift first
        // and only the main-key release closes the press.
        lock (_lock)
        {
            if (set.PromptPaletteKey is not null && key == set.PromptPaletteKey.Value)
            {
                _promptKeyDown = false;
            }

            if (set.RecentTranscriptionsKey is not null && key == set.RecentTranscriptionsKey.Value)
            {
                _recentKeyDown = false;
            }

            if (
                set.CopyLastTranscriptionKey is not null
                && key == set.CopyLastTranscriptionKey.Value
            )
            {
                _copyLastKeyDown = false;
            }

            if (set.TransformSelectionKey is not null && key == set.TransformSelectionKey.Value)
            {
                _transformSelectionKeyDown = false;
            }

            if (key == set.CancelKey)
            {
                _cancelKeyDown = false;
            }

            // Clear the press-time entry without consulting the current
            // shortcut set — if the action was edited/removed mid-hold,
            // set.PromptActionHotkeys may no longer reference this key,
            // but our own dictionary still remembers the press and must
            // release it to keep dedup honest for the next press.
            _promptActionKeyDown.Remove(key);
        }

        if (key != set.DictationKey)
        {
            return;
        }

        DateTime keyDownAt;
        lock (_lock)
        {
            if (!_dictationKeyDown)
            {
                return;
            }

            _dictationKeyDown = false;
            keyDownAt = _dictationKeyDownTime;
        }

        var heldMs = (DateTime.UtcNow - keyDownAt).TotalMilliseconds;
        switch (set.Mode)
        {
            case RecordingMode.PushToTalk:
                Raise(DictationStopRequested, nameof(DictationStopRequested));
                break;
            case RecordingMode.Hybrid:
                if (heldMs >= PushToTalkThresholdMs)
                {
                    Raise(DictationStopRequested, nameof(DictationStopRequested));
                }

                break;
            case RecordingMode.Toggle:
                // No-op — Toggle is handled on press.
                break;
        }
    }

    private bool TryClaimKeyDown(ref bool flag)
    {
        lock (_lock)
        {
            if (flag)
            {
                return false;
            }

            flag = true;
            return true;
        }
    }

    private static void Raise(Action? handler, string name)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShortcutDispatcher] {name} handler threw: {ex.Message}");
        }
    }

    private void RaisePromptAction(string actionId)
    {
        var handler = PromptActionRequested;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(actionId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ShortcutDispatcher] {nameof(PromptActionRequested)} handler threw: {ex.Message}"
            );
        }
    }
}