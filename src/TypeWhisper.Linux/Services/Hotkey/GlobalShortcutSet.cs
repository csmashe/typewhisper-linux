using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Backend-neutral snapshot of every configured global shortcut. The
///     coordinator (<see cref="HotkeyService" />) owns the source-of-truth state
///     and pushes a new <see cref="GlobalShortcutSet" /> to the active backend
///     whenever any binding changes.
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
    bool IsCancelEnabled,
    IReadOnlyList<PromptActionHotkey> PromptActionHotkeys
)
{
    public GlobalShortcutSet(
        KeyCode dictationKey,
        ModifierMask dictationModifiers,
        KeyCode? promptPaletteKey,
        ModifierMask promptPaletteModifiers,
        KeyCode? recentTranscriptionsKey,
        ModifierMask recentTranscriptionsModifiers,
        KeyCode? copyLastTranscriptionKey,
        ModifierMask copyLastTranscriptionModifiers,
        KeyCode? transformSelectionKey,
        ModifierMask transformSelectionModifiers,
        KeyCode cancelKey,
        ModifierMask cancelModifiers,
        RecordingMode mode,
        bool isCancelEnabled
    )
        : this(
            dictationKey,
            dictationModifiers,
            promptPaletteKey,
            promptPaletteModifiers,
            recentTranscriptionsKey,
            recentTranscriptionsModifiers,
            copyLastTranscriptionKey,
            copyLastTranscriptionModifiers,
            transformSelectionKey,
            transformSelectionModifiers,
            cancelKey,
            cancelModifiers,
            mode,
            isCancelEnabled,
            Array.Empty<PromptActionHotkey>()
        )
    {
    }
}

/// <summary>
///     A prompt action bound to a direct-execution hotkey (B12). Pressing the
///     chord captures the current selection and runs the action against it,
///     bypassing the palette UI. <see cref="ActionId" /> is the
///     <c>PromptAction.Id</c> the matched chord should execute.
/// </summary>
public sealed record PromptActionHotkey(string ActionId, KeyCode Key, ModifierMask Modifiers);