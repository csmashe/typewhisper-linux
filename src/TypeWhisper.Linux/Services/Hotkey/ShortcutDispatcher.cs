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
    // Profile-hotkey dedup, keyed by the physical KeyCode matched at press
    // time — same reasoning as _promptActionKeyDown above (release-time
    // cleanup is independent of the current shortcut set). The dictation
    // variant also records the recording mode and when the key went down so
    // the release-time hold duration can be computed for PushToTalk/Hybrid
    // against the press-time mode, mirroring the main dictation key's
    // _dictationKeyDownTime.
    private readonly Dictionary<KeyCode, (string ProfileId, RecordingMode Mode, DateTime DownAt)>
        _profileDictationKeyDown = new();
    private readonly Dictionary<KeyCode, string> _profileTextKeyDown = new();
    private bool _recentKeyDown;

    private GlobalShortcutSet? _shortcuts;
    private bool _transformSelectionKeyDown;

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

    public event Action? DictationToggleRequested;
    public event Action? DictationStartRequested;
    public event Action? DictationStopRequested;
    public event Action? PromptPaletteRequested;
    public event Action? TransformSelectionRequested;
    public event Action? RecentTranscriptionsRequested;
    public event Action? CopyLastTranscriptionRequested;
    public event Action? CancelRequested;
    public event Action<string>? PromptActionRequested;

    // Profile hotkeys. The dictation variants carry the forced profile id to
    // start/toggle; stop is parameterless because the id was already consumed
    // when the session started (mirrors DictationStopRequested).
    public event Action<string>? ProfileDictationToggleRequested;
    public event Action<string>? ProfileDictationStartRequested;
    public event Action? ProfileDictationStopRequested;
    public event Action<string>? ProfileTextProcessingRequested;

    private void HandlePress(KeyCode key, ModifierMask mods, GlobalShortcutSet set)
    {
        var match = ShortcutMatcher.Match(
            key,
            mods,
            set,
            out var promptActionId,
            out var profileId,
            out var profileBehavior
        );

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
                out promptActionId,
                out profileId,
                out profileBehavior
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
            case ShortcutMatchKind.Profile:
                if (profileId is null)
                {
                    return;
                }

                if (profileBehavior == ProfileHotkeyBehavior.ProcessSelectedText)
                {
                    lock (_lock)
                    {
                        if (_profileTextKeyDown.ContainsKey(key))
                        {
                            return;
                        }

                        _profileTextKeyDown[key] = profileId;
                    }

                    RaiseProfile(
                        ProfileTextProcessingRequested,
                        profileId,
                        nameof(ProfileTextProcessingRequested)
                    );
                    return;
                }

                // StartDictation: mirror the main Dictation case so a profile
                // dictation hotkey obeys the same recording mode, but carry the
                // forced profile id on start/toggle.
                lock (_lock)
                {
                    if (_profileDictationKeyDown.ContainsKey(key))
                    {
                        return;
                    }

                    // Capture the recording mode at press time so the release
                    // path switches on the mode that was active when the key
                    // went down — not a mode the user may have changed mid-hold.
                    _profileDictationKeyDown[key] = (profileId, set.Mode, DateTime.UtcNow);
                }

                switch (set.Mode)
                {
                    case RecordingMode.Toggle:
                        RaiseProfile(
                            ProfileDictationToggleRequested,
                            profileId,
                            nameof(ProfileDictationToggleRequested)
                        );
                        break;
                    case RecordingMode.PushToTalk:
                        RaiseProfile(
                            ProfileDictationStartRequested,
                            profileId,
                            nameof(ProfileDictationStartRequested)
                        );
                        break;
                    case RecordingMode.Hybrid:
                        // Always toggle on press; if held past the threshold,
                        // HandleRelease additionally fires Stop — same as the
                        // main dictation key.
                        RaiseProfile(
                            ProfileDictationToggleRequested,
                            profileId,
                            nameof(ProfileDictationToggleRequested)
                        );
                        break;
                }

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

            // ProcessSelectedText profile hotkeys fire on key-down only; just
            // clear the dedup entry so the next press is honored.
            _profileTextKeyDown.Remove(key);
        }

        // StartDictation profile hotkeys mirror the main dictation key's
        // release semantics, but keyed off the physical key so an edit/remove
        // mid-hold still releases cleanly.
        (string ProfileId, RecordingMode Mode, DateTime DownAt) profileHeld;
        bool hadProfileDictation;
        lock (_lock)
        {
            hadProfileDictation = _profileDictationKeyDown.Remove(key, out profileHeld);
        }

        if (hadProfileDictation)
        {
            var profileHeldMs = (DateTime.UtcNow - profileHeld.DownAt).TotalMilliseconds;
            // Use the mode captured at press time, not the possibly-changed
            // current set.Mode, so a mid-hold mode switch can't make the
            // release fire a Stop the press never set up.
            switch (profileHeld.Mode)
            {
                case RecordingMode.PushToTalk:
                    Raise(
                        ProfileDictationStopRequested,
                        nameof(ProfileDictationStopRequested)
                    );
                    break;
                case RecordingMode.Hybrid:
                    if (profileHeldMs >= PushToTalkThresholdMs)
                    {
                        Raise(
                            ProfileDictationStopRequested,
                            nameof(ProfileDictationStopRequested)
                        );
                    }

                    break;
                case RecordingMode.Toggle:
                    // No-op — Toggle is handled on press.
                    break;
            }
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

    private static void RaiseProfile(Action<string>? handler, string profileId, string name)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(profileId);
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