using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Backend-neutral snapshot of every configured global shortcut. <see cref="HotkeyService" />
///     owns the source-of-truth state and pushes a new instance to the active backend on any change.
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
    IReadOnlyList<PromptActionHotkey> PromptActionHotkeys,
    IReadOnlyList<ProfileHotkey> ProfileHotkeys
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
            Array.Empty<PromptActionHotkey>(),
            Array.Empty<ProfileHotkey>()
        )
    {
    }
}

/// <summary>
///     A prompt action bound to a direct-execution hotkey. Pressing the chord captures the current
///     selection and runs the action against it, bypassing the palette UI.
/// </summary>
public sealed record PromptActionHotkey(string ActionId, KeyCode Key, ModifierMask Modifiers);

/// <summary>
///     A Profile bound to a global hotkey. <see cref="Behavior" /> controls whether the chord starts
///     dictation for this profile or runs its linked prompt action on the current selection.
///     Travels with the chord so the dispatcher doesn't need to look it up at fire time.
/// </summary>
public sealed record ProfileHotkey(
    string ProfileId,
    KeyCode Key,
    ModifierMask Modifiers,
    ProfileHotkeyBehavior Behavior);