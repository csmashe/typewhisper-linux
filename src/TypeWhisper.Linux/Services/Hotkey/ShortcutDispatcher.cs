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
    // Hybrid mode: holds shorter than this are Toggle; longer holds are Push-to-Talk.
    // 600 ms is a deliberate tap-vs-hold boundary — avoids misfires on quick taps,
    // still feels responsive when held.
    private const int PushToTalkThresholdMs = 600;

    private readonly object _lock = new();

    // Profile dictation dedup, keyed by physical KeyCode at press time. Also records the
    // recording mode and timestamp so the release path can compute hold duration for
    // PushToTalk/Hybrid using the press-time mode (mirrors _dictationKeyDownTime).
    private readonly Dictionary<KeyCode, (string ProfileId, RecordingMode Mode, DateTime DownAt)>
        _profileDictationKeyDown = new();

    private readonly Dictionary<KeyCode, string> _profileTextKeyDown = new();

    // Per-action key-down dedup, keyed by the physical KeyCode at press time.
    // Using the press-time key means release-time cleanup works even if the user
    // edits or removes the binding mid-hold — otherwise the stranded entry would
    // silently suppress all future presses of that action.
    private readonly Dictionary<KeyCode, string> _promptActionKeyDown = new();
    private bool _cancelKeyDown;
    private bool _copyLastKeyDown;
    private bool _dictationKeyDown;
    private DateTime _dictationKeyDownTime;
    private bool _promptKeyDown;
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

                    // Capture mode at press time so a mid-hold mode change can't
                    // affect the release path.
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
                        // Toggle on press; HandleRelease fires Stop if held past threshold.
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
                        // Toggle on press; HandleRelease fires Stop if held past threshold.
                        Raise(DictationToggleRequested, nameof(DictationToggleRequested));
                        break;
                }

                return;
        }
    }

    private void HandleRelease(KeyCode key, GlobalShortcutSet set)
    {
        // Clear repeat-guards on key release (modifier-only releases are ignored).
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

            // Clear press-time entries regardless of the current shortcut set —
            // an edit/remove mid-hold must not strand the entry and suppress future presses.
            _promptActionKeyDown.Remove(key);
            _profileTextKeyDown.Remove(key);
        }

        // Profile dictation release mirrors main dictation key semantics.
        (string ProfileId, RecordingMode Mode, DateTime DownAt) profileHeld;
        bool hadProfileDictation;
        lock (_lock)
        {
            hadProfileDictation = _profileDictationKeyDown.Remove(key, out profileHeld);
        }

        if (hadProfileDictation)
        {
            var profileHeldMs = (DateTime.UtcNow - profileHeld.DownAt).TotalMilliseconds;
            // Use press-time mode; a mid-hold mode switch must not fire a Stop the press never set up.
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