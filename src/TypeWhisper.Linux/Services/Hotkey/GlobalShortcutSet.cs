using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Backend-neutral snapshot of every configured global shortcut. The
/// coordinator (<see cref="HotkeyService"/>) owns the source-of-truth state
/// and pushes a new <see cref="GlobalShortcutSet"/> to the active backend
/// whenever any binding changes.
/// </summary>
public sealed record GlobalShortcutSet(
    KeyCode DictationKey,
    ModifierMask DictationModifiers,
    KeyCode? PromptPaletteKey,
    ModifierMask PromptPaletteModifiers,
    KeyCode? RecentTranscriptionsKey,
    ModifierMask RecentTranscriptionsModifiers,
    KeyCode? CopyLastTranscriptionKey,
    ModifierMask CopyLastTranscriptionModifiers,
    KeyCode? TransformSelectionKey,
    ModifierMask TransformSelectionModifiers,
    KeyCode CancelKey,
    ModifierMask CancelModifiers,
    RecordingMode Mode,
    bool IsCancelEnabled);
