using SharpHook.Native;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Which configured binding a (key, modifier) tuple resolves to.
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
    PromptAction,
    Profile
}

/// <summary>
///     Backend-neutral, pure/stateless chord matching. Given a (KeyCode, ModifierMask)
///     and the current <see cref="GlobalShortcutSet" />, returns which binding fires.
///     Both SharpHook and evdev backends call this after translating their native events.
/// </summary>
internal static class ShortcutMatcher
{
    public static ShortcutMatchKind Match(
        KeyCode key,
        ModifierMask pressedMods,
        GlobalShortcutSet set
    )
    {
        return Match(key, pressedMods, set, out _, out _, out _);
    }

    /// <summary>
    ///     Overload that also returns <c>PromptAction.Id</c> / profile ID when matched.
    ///     The dispatcher uses this; callers that only need the kind use the simpler overload.
    /// </summary>
    public static ShortcutMatchKind Match(
        KeyCode key,
        ModifierMask pressedMods,
        GlobalShortcutSet set,
        out string? promptActionId,
        out string? profileId,
        out ProfileHotkeyBehavior profileBehavior
    )
    {
        promptActionId = null;
        profileId = null;
        profileBehavior = default;

        // Cancel takes priority so an active dictation can be discarded even on a collision.
        if (key == set.CancelKey && ModifiersMatch(pressedMods, set.CancelModifiers))
        {
            return ShortcutMatchKind.Cancel;
        }

        if (
            Matches(
                key,
                pressedMods,
                set.RecentTranscriptionsKey,
                set.RecentTranscriptionsModifiers
            )
        )
        {
            return ShortcutMatchKind.RecentTranscriptions;
        }

        if (
            Matches(
                key,
                pressedMods,
                set.CopyLastTranscriptionKey,
                set.CopyLastTranscriptionModifiers
            )
        )
        {
            return ShortcutMatchKind.CopyLastTranscription;
        }

        if (Matches(key, pressedMods, set.TransformSelectionKey, set.TransformSelectionModifiers))
        {
            return ShortcutMatchKind.TransformSelection;
        }

        if (Matches(key, pressedMods, set.PromptPaletteKey, set.PromptPaletteModifiers))
        {
            return ShortcutMatchKind.PromptPalette;
        }

        // Prompt-action hotkeys sit before Dictation so a profile chord can't shadow them.
        // HotkeyService de-duplicates before pushing, so linear scan is fine (N < 10).
        foreach (var entry in set.PromptActionHotkeys)
        {
            if (key == entry.Key && ModifiersMatch(pressedMods, entry.Modifiers))
            {
                promptActionId = entry.ActionId;
                return ShortcutMatchKind.PromptAction;
            }
        }

        // Profile hotkeys after prompt-actions; SetProfileHotkeys already rejects collisions,
        // but the ordering preserves that intent.
        foreach (var entry in set.ProfileHotkeys)
        {
            if (key == entry.Key && ModifiersMatch(pressedMods, entry.Modifiers))
            {
                profileId = entry.ProfileId;
                profileBehavior = entry.Behavior;
                return ShortcutMatchKind.Profile;
            }
        }

        if (key == set.DictationKey && ModifiersMatch(pressedMods, set.DictationModifiers))
        {
            return ShortcutMatchKind.Dictation;
        }

        return ShortcutMatchKind.None;
    }

    public static bool CancelCollidesWithAnyBinding(GlobalShortcutSet set)
    {
        var pressedMods = set.CancelModifiers;
        var key = set.CancelKey;
        if (
            Matches(key, pressedMods, set.DictationKey, set.DictationModifiers)
            || Matches(key, pressedMods, set.PromptPaletteKey, set.PromptPaletteModifiers)
            || Matches(
                key,
                pressedMods,
                set.RecentTranscriptionsKey,
                set.RecentTranscriptionsModifiers
            )
            || Matches(
                key,
                pressedMods,
                set.CopyLastTranscriptionKey,
                set.CopyLastTranscriptionModifiers
            )
            || Matches(
                key,
                pressedMods,
                set.TransformSelectionKey,
                set.TransformSelectionModifiers
            )
        )
        {
            return true;
        }

        return set.PromptActionHotkeys.Any(entry => key == entry.Key && ModifiersMatch(pressedMods, entry.Modifiers))
               || set.ProfileHotkeys.Any(entry => key == entry.Key && ModifiersMatch(pressedMods, entry.Modifiers));
    }

    /// <summary>
    ///     Matches on Ctrl/Shift/Alt/Meta only; ignores NumLock/CapsLock bits
    ///     that may be latched in <paramref name="pressed" />.
    /// </summary>
    public static bool ModifiersMatch(ModifierMask pressed, ModifierMask required)
    {
        return HasCtrl(pressed) == HasCtrl(required)
               && HasShift(pressed) == HasShift(required)
               && HasAlt(pressed) == HasAlt(required)
               && HasMeta(pressed) == HasMeta(required);
    }

    private static bool Matches(
        KeyCode key,
        ModifierMask pressedMods,
        KeyCode? targetKey,
        ModifierMask targetMods
    )
    {
        if (targetKey is null)
        {
            return false;
        }

        return key == targetKey.Value && ModifiersMatch(pressedMods, targetMods);
    }

    private static bool HasCtrl(ModifierMask mask)
    {
        return mask.HasFlag(ModifierMask.LeftCtrl) || mask.HasFlag(ModifierMask.RightCtrl);
    }

    private static bool HasShift(ModifierMask mask)
    {
        return mask.HasFlag(ModifierMask.LeftShift) || mask.HasFlag(ModifierMask.RightShift);
    }

    private static bool HasAlt(ModifierMask mask)
    {
        return mask.HasFlag(ModifierMask.LeftAlt) || mask.HasFlag(ModifierMask.RightAlt);
    }

    private static bool HasMeta(ModifierMask mask)
    {
        return mask.HasFlag(ModifierMask.LeftMeta) || mask.HasFlag(ModifierMask.RightMeta);
    }
}