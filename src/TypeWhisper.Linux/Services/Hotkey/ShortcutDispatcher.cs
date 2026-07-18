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

    private readonly Lock _lock = new();

    // Profile dictation dedup, keyed by physical KeyCode at press time. Also records the
    // recording mode and timestamp so the release path can compute hold duration for
    // PushToTalk/Hybrid using the press-time mode (mirrors _mainDictationHeld).
    private readonly Dictionary<KeyCode, (string ProfileId, RecordingMode Mode, DateTime DownAt)>
        _profileDictationKeyDown = new();

    private readonly Dictionary<KeyCode, PendingSelectionWorkflow> _pendingSelectionWorkflows =
        new();
    private bool _cancelKeyDown;
    private bool _copyLastKeyDown;
    private (KeyCode Key, RecordingMode Mode, DateTime DownAt)? _mainDictationHeld;
    private bool _recentKeyDown;

    private GlobalShortcutSet? _shortcuts;

    public void UpdateShortcuts(GlobalShortcutSet shortcuts)
    {
        Volatile.Write(ref _shortcuts, shortcuts);
    }

    public void ClearShortcuts()
    {
        Volatile.Write(ref _shortcuts, null);

        // Drop any release-gated selection workflow that was queued before the unregister.
        // Handle ignores releases while the set is null, so a pending entry would otherwise
        // survive into the next registration and either suppress the rebound key (stale TryAdd)
        // or dispatch its pre-unregister payload against the current selection on release.
        lock (_lock)
        {
            _pendingSelectionWorkflows.Clear();
        }
    }

    /// <summary>
    ///     Clears physical key-down bookkeeping when an input source is detached without release
    ///     transitions (e.g. session lock), and unconditionally requests a dictation discard: a
    ///     Toggle (or short Hybrid tap) recording outlives its key press, so held-key state
    ///     cannot tell whether one is active. Discard (not stop) so nothing is transcribed or
    ///     typed into the lock screen; idempotent when nothing is recording.
    /// </summary>
    public void ResetState()
    {
        lock (_lock)
        {
            _profileDictationKeyDown.Clear();
            _pendingSelectionWorkflows.Clear();
            _cancelKeyDown = false;
            _copyLastKeyDown = false;
            _mainDictationHeld = null;
            _recentKeyDown = false;
        }

        // Main and profile dictation share one recording session, so a single discard covers both.
        Raise(DictationDiscardRequested, nameof(DictationDiscardRequested));
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
            HandleRelease(key, mods, set);
        }
    }

    public event Action? DictationToggleRequested;
    public event Action? DictationStartRequested;
    public event Action? DictationStopRequested;

    // Raised only by ResetState on session-loss teardown: discard the recording without
    // transcription or text insertion (distinct from the user-driven stop/cancel keys).
    public event Action? DictationDiscardRequested;
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

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- only the actionable cases are handled; remaining enum values are deliberate no-ops.
        switch (match)
        {
            case ShortcutMatchKind.PromptAction:
                if (promptActionId is null)
                {
                    return;
                }

                TryClaimSelectionWorkflow(
                    key,
                    SelectionWorkflowKind.PromptAction,
                    promptActionId
                );
                return;
            case ShortcutMatchKind.Profile:
                if (profileId is null)
                {
                    return;
                }

                if (profileBehavior == ProfileHotkeyBehavior.ProcessSelectedText)
                {
                    TryClaimSelectionWorkflow(
                        key,
                        SelectionWorkflowKind.ProfileTextProcessing,
                        profileId
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

                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- all defined RecordingMode values are handled; the default (out-of-range) branch is intentionally omitted.
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
                TryClaimSelectionWorkflow(key, SelectionWorkflowKind.TransformSelection);
                return;

            case ShortcutMatchKind.PromptPalette:
                TryClaimSelectionWorkflow(key, SelectionWorkflowKind.PromptPalette);
                return;

            case ShortcutMatchKind.Dictation:
                lock (_lock)
                {
                    if (_mainDictationHeld is not null)
                    {
                        return;
                    }

                    _mainDictationHeld = (key, set.Mode, DateTime.UtcNow);
                }

                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- all defined RecordingMode values are handled; the default (out-of-range) branch is intentionally omitted.
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

    private void HandleRelease(KeyCode key, ModifierMask mods, GlobalShortcutSet set)
    {
        List<PendingSelectionWorkflow>? readySelectionWorkflows = null;

        lock (_lock)
        {
            if (_pendingSelectionWorkflows.TryGetValue(key, out var releasedWorkflow))
            {
                _pendingSelectionWorkflows[key] = releasedWorkflow with
                {
                    TriggerReleased = true
                };
            }

            if (ShortcutMatcher.ModifiersMatch(mods, ModifierMask.None))
            {
                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- explicit loop keeps the Dictionary.ValueCollection enumerator (no boxing) and reads clearly under _lock; the LINQ form would switch enumerators for no gain.
                foreach (var pending in _pendingSelectionWorkflows.Values)
                {
                    if (!pending.TriggerReleased)
                    {
                        continue;
                    }

                    (readySelectionWorkflows ??= []).Add(pending);
                }

                if (readySelectionWorkflows is not null)
                {
                    foreach (var pending in readySelectionWorkflows)
                    {
                        _pendingSelectionWorkflows.Remove(pending.TriggerKey);
                    }
                }
            }

            // Clear non-selection repeat-guards on their terminal-key release.
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

            if (key == set.CancelKey)
            {
                _cancelKeyDown = false;
            }
        }

        if (readySelectionWorkflows is not null)
        {
            foreach (var pending in readySelectionWorkflows)
            {
                DispatchSelectionWorkflow(pending);
            }
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
            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- all defined RecordingMode values are handled; the default (out-of-range) branch is intentionally omitted.
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

        (KeyCode Key, RecordingMode Mode, DateTime DownAt) held;
        lock (_lock)
        {
            var current = _mainDictationHeld;
            if (!current.HasValue || current.Value.Key != key)
            {
                return;
            }

            held = current.Value;
            _mainDictationHeld = null;
        }

        var heldMs = (DateTime.UtcNow - held.DownAt).TotalMilliseconds;
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- all defined RecordingMode values are handled; the default (out-of-range) branch is intentionally omitted.
        switch (held.Mode)
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

    // ReSharper disable once UnusedMethodReturnValue.Local -- the bool completes the Try* contract (mirrors TryClaimKeyDown); callers deliberately rely only on the idempotent claim side effect that de-dupes key auto-repeat.
    private bool TryClaimSelectionWorkflow(
        KeyCode key,
        SelectionWorkflowKind kind,
        string? payload = null
    )
    {
        lock (_lock)
        {
            return _pendingSelectionWorkflows.TryAdd(
                key,
                new PendingSelectionWorkflow(key, kind, payload, false)
            );
        }
    }

    private void DispatchSelectionWorkflow(PendingSelectionWorkflow pending)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- all defined SelectionWorkflowKind values are handled; the default (out-of-range) branch is intentionally omitted.
        switch (pending.Kind)
        {
            case SelectionWorkflowKind.PromptPalette:
                Raise(PromptPaletteRequested, nameof(PromptPaletteRequested));
                break;
            case SelectionWorkflowKind.PromptAction:
                RaisePromptAction(pending.Payload!);
                break;
            case SelectionWorkflowKind.ProfileTextProcessing:
                RaiseProfile(
                    ProfileTextProcessingRequested,
                    pending.Payload!,
                    nameof(ProfileTextProcessingRequested)
                );
                break;
            case SelectionWorkflowKind.TransformSelection:
                Raise(TransformSelectionRequested, nameof(TransformSelectionRequested));
                break;
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

    private enum SelectionWorkflowKind
    {
        PromptPalette,
        PromptAction,
        ProfileTextProcessing,
        TransformSelection
    }

    private readonly record struct PendingSelectionWorkflow(
        KeyCode TriggerKey,
        SelectionWorkflowKind Kind,
        string? Payload,
        bool TriggerReleased
    );
}
