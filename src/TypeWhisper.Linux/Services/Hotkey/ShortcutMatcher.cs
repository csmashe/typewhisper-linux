using SharpHook.Native;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Which configured binding a (key, modifier) tuple resolves to.
/// </summary>
internal enum ShortcutMatchKind
{
    None,
    Dictation,
    PromptPalette,
    RecentTranscriptions,
    CopyLastTranscription,
    TransformSelection,
    Cancel,
}

/// <summary>
/// Backend-neutral matching: given a (KeyCode, ModifierMask) and the current
/// <see cref="GlobalShortcutSet"/>, return which binding (if any) the chord
/// resolves to. Pure, stateless — both the SharpHook backend and the
/// upcoming evdev backend (Phase 2) call this once they have translated
/// their native event into the backend-neutral tuple.
/// </summary>
internal static class ShortcutMatcher
{
    public static ShortcutMatchKind Match(KeyCode key, ModifierMask pressedMods, GlobalShortcutSet set)
    {
        // Order matters: cancel takes priority so an active dictation can be
        // discarded even if the cancel key collides with another binding —
        // the caller still gets to decide whether to honor it.
        if (key == set.CancelKey && ModifiersMatch(pressedMods, set.CancelModifiers))
            return ShortcutMatchKind.Cancel;

        if (Matches(key, pressedMods, set.RecentTranscriptionsKey, set.RecentTranscriptionsModifiers))
            return ShortcutMatchKind.RecentTranscriptions;

        if (Matches(key, pressedMods, set.CopyLastTranscriptionKey, set.CopyLastTranscriptionModifiers))
            return ShortcutMatchKind.CopyLastTranscription;

        if (Matches(key, pressedMods, set.TransformSelectionKey, set.TransformSelectionModifiers))
            return ShortcutMatchKind.TransformSelection;

        if (Matches(key, pressedMods, set.PromptPaletteKey, set.PromptPaletteModifiers))
            return ShortcutMatchKind.PromptPalette;

        if (key == set.DictationKey && ModifiersMatch(pressedMods, set.DictationModifiers))
            return ShortcutMatchKind.Dictation;

        return ShortcutMatchKind.None;
    }

    private static bool Matches(KeyCode key, ModifierMask pressedMods, KeyCode? targetKey, ModifierMask targetMods)
    {
        if (targetKey is null) return false;
        return key == targetKey.Value && ModifiersMatch(pressedMods, targetMods);
    }

    public static bool CancelCollidesWithAnyBinding(GlobalShortcutSet set)
    {
        var pressedMods = set.CancelModifiers;
        var key = set.CancelKey;
        return Matches(key, pressedMods, set.DictationKey, set.DictationModifiers)
            || Matches(key, pressedMods, set.PromptPaletteKey, set.PromptPaletteModifiers)
            || Matches(key, pressedMods, set.RecentTranscriptionsKey, set.RecentTranscriptionsModifiers)
            || Matches(key, pressedMods, set.CopyLastTranscriptionKey, set.CopyLastTranscriptionModifiers)
            || Matches(key, pressedMods, set.TransformSelectionKey, set.TransformSelectionModifiers);
    }

    /// <summary>
    /// Exact match on the four modifier groups (Ctrl/Shift/Alt/Meta). Other
    /// bits like NumLock/CapsLock in <paramref name="pressed"/> must be
    /// ignored since the user might have them latched.
    /// </summary>
    public static bool ModifiersMatch(ModifierMask pressed, ModifierMask required)
    {
        return HasCtrl(pressed) == HasCtrl(required)
            && HasShift(pressed) == HasShift(required)
            && HasAlt(pressed) == HasAlt(required)
            && HasMeta(pressed) == HasMeta(required);
    }

    private static bool HasCtrl(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftCtrl) || mask.HasFlag(ModifierMask.RightCtrl);

    private static bool HasShift(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftShift) || mask.HasFlag(ModifierMask.RightShift);

    private static bool HasAlt(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftAlt) || mask.HasFlag(ModifierMask.RightAlt);

    private static bool HasMeta(ModifierMask mask) =>
        mask.HasFlag(ModifierMask.LeftMeta) || mask.HasFlag(ModifierMask.RightMeta);
}
