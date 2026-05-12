using System.Diagnostics;
using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Backend-neutral press/release state machine: takes a stream of
/// <c>(KeyCode, ModifierMask, pressed)</c> tuples, matches against the
/// current <see cref="GlobalShortcutSet"/>, applies the recording-mode
/// state machine, and raises typed events. Owned by both SharpHook and
/// evdev backends so the user-visible behavior is identical regardless of
/// the event source.
/// </summary>
internal sealed class ShortcutDispatcher
{
    private const int PushToTalkThresholdMs = 600;

    private readonly object _lock = new();

    private GlobalShortcutSet? _shortcuts;
    private bool _dictationKeyDown;
    private bool _promptKeyDown;
    private bool _recentKeyDown;
    private bool _copyLastKeyDown;
    private bool _transformSelectionKeyDown;
    private bool _cancelKeyDown;
    private DateTime _dictationKeyDownTime;

    public event Action? DictationToggleRequested;
    public event Action? DictationStartRequested;
    public event Action? DictationStopRequested;
    public event Action? PromptPaletteRequested;
    public event Action? TransformSelectionRequested;
    public event Action? RecentTranscriptionsRequested;
    public event Action? CopyLastTranscriptionRequested;
    public event Action? CancelRequested;

    public void UpdateShortcuts(GlobalShortcutSet shortcuts) =>
        Volatile.Write(ref _shortcuts, shortcuts);

    public void ClearShortcuts() => Volatile.Write(ref _shortcuts, null);

    /// <summary>
    /// Drives the state machine from a backend-neutral key event. Returns
    /// silently if no shortcut set is currently registered.
    /// </summary>
    public void Handle(KeyCode key, ModifierMask mods, bool pressed)
    {
        var set = Volatile.Read(ref _shortcuts);
        if (set is null) return;

        if (pressed)
            HandlePress(key, mods, set);
        else
            HandleRelease(key, set);
    }

    private void HandlePress(KeyCode key, ModifierMask mods, GlobalShortcutSet set)
    {
        var match = ShortcutMatcher.Match(key, mods, set);

        // Cancel: only fires while a dictation is active and only when it
        // doesn't collide with another binding — otherwise we fall through
        // so the regular matcher handles the press.
        if (match == ShortcutMatchKind.Cancel)
        {
            lock (_lock)
            {
                if (_cancelKeyDown) return;
                _cancelKeyDown = true;
            }

            if (set.IsCancelEnabled && !ShortcutMatcher.CancelCollidesWithAnyBinding(set))
            {
                Raise(CancelRequested, nameof(CancelRequested));
                return;
            }

            // Cancel collides with a configured binding — re-match without
            // cancel so that other binding can fire.
            match = ShortcutMatcher.Match(key, mods, set with { CancelKey = KeyCode.VcUndefined });
        }

        switch (match)
        {
            case ShortcutMatchKind.RecentTranscriptions:
                if (!TryClaimKeyDown(ref _recentKeyDown)) return;
                Raise(RecentTranscriptionsRequested, nameof(RecentTranscriptionsRequested));
                return;

            case ShortcutMatchKind.CopyLastTranscription:
                if (!TryClaimKeyDown(ref _copyLastKeyDown)) return;
                Raise(CopyLastTranscriptionRequested, nameof(CopyLastTranscriptionRequested));
                return;

            case ShortcutMatchKind.TransformSelection:
                if (!TryClaimKeyDown(ref _transformSelectionKeyDown)) return;
                Raise(TransformSelectionRequested, nameof(TransformSelectionRequested));
                return;

            case ShortcutMatchKind.PromptPalette:
                if (!TryClaimKeyDown(ref _promptKeyDown)) return;
                Raise(PromptPaletteRequested, nameof(PromptPaletteRequested));
                return;

            case ShortcutMatchKind.Dictation:
                bool claimed;
                lock (_lock)
                {
                    if (_dictationKeyDown) return;
                    _dictationKeyDown = true;
                    _dictationKeyDownTime = DateTime.UtcNow;
                    claimed = true;
                }
                if (!claimed) return;

                switch (set.Mode)
                {
                    case RecordingMode.Toggle:
                        Raise(DictationToggleRequested, nameof(DictationToggleRequested));
                        break;
                    case RecordingMode.PushToTalk:
                        Raise(DictationStartRequested, nameof(DictationStartRequested));
                        break;
                    case RecordingMode.Hybrid:
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
                _promptKeyDown = false;
            if (set.RecentTranscriptionsKey is not null && key == set.RecentTranscriptionsKey.Value)
                _recentKeyDown = false;
            if (set.CopyLastTranscriptionKey is not null && key == set.CopyLastTranscriptionKey.Value)
                _copyLastKeyDown = false;
            if (set.TransformSelectionKey is not null && key == set.TransformSelectionKey.Value)
                _transformSelectionKeyDown = false;
            if (key == set.CancelKey)
                _cancelKeyDown = false;
        }

        if (key != set.DictationKey) return;

        DateTime keyDownAt;
        lock (_lock)
        {
            if (!_dictationKeyDown) return;
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
                    Raise(DictationStopRequested, nameof(DictationStopRequested));
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
            if (flag) return false;
            flag = true;
            return true;
        }
    }

    private static void Raise(Action? handler, string name)
    {
        if (handler is null) return;
        try { handler(); }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShortcutDispatcher] {name} handler threw: {ex.Message}");
        }
    }
}
