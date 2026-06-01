using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutMatcherTests
{
    [Fact]
    public void Match_DefaultBinding_IdentifiesDictation()
    {
        var kind = ShortcutMatcher.Match(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            DefaultSet()
        );

        Assert.Equal(ShortcutMatchKind.Dictation, kind);
    }

    [Fact]
    public void Match_EscapeWithNoModifiers_IdentifiesCancel()
    {
        var kind = ShortcutMatcher.Match(KeyCode.VcEscape, ModifierMask.None, DefaultSet());

        Assert.Equal(ShortcutMatchKind.Cancel, kind);
    }

    [Fact]
    public void Match_UnrelatedKey_ReturnsNone()
    {
        var kind = ShortcutMatcher.Match(KeyCode.VcA, ModifierMask.None, DefaultSet());

        Assert.Equal(ShortcutMatchKind.None, kind);
    }

    [Fact]
    public void Match_PromptActionTakesPriorityOverDictation()
    {
        // Same chord as dictation but bound as a prompt-action — the
        // prompt-action arm sits between PromptPalette and Dictation in
        // priority, so the dictation match must NOT win.
        var set = new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            RecordingMode.Toggle,
            false,
            new[]
            {
                new PromptActionHotkey(
                    "alpha",
                    KeyCode.VcSpace,
                    ModifierMask.LeftCtrl | ModifierMask.LeftShift
                )
            }
        );

        var kind = ShortcutMatcher.Match(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            set,
            out var actionId
        );

        Assert.Equal(ShortcutMatchKind.PromptAction, kind);
        Assert.Equal("alpha", actionId);
    }

    [Fact]
    public void Match_RightCtrlSubstitutesForLeftCtrl()
    {
        // Live keyboards routinely report RightCtrl for chords pressed on the
        // right side; the matcher must treat the two as interchangeable so
        // configured Ctrl+Shift+Space still fires.
        var kind = ShortcutMatcher.Match(
            KeyCode.VcSpace,
            ModifierMask.RightCtrl | ModifierMask.RightShift,
            DefaultSet()
        );

        Assert.Equal(ShortcutMatchKind.Dictation, kind);
    }

    [Fact]
    public void Match_SideSpecificSingleModifierBinding_MatchesOnlyThatSide()
    {
        // B8: when the dictation key IS a side-specific modifier (e.g.
        // VcRightAlt with no mods), the matcher distinguishes left/right
        // naturally because the key field — not the modifier mask — carries
        // the side information.
        var set = new GlobalShortcutSet(
            KeyCode.VcRightAlt,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            RecordingMode.Toggle,
            false
        );

        var rightMatch = ShortcutMatcher.Match(KeyCode.VcRightAlt, ModifierMask.None, set);
        var leftMatch = ShortcutMatcher.Match(KeyCode.VcLeftAlt, ModifierMask.None, set);

        Assert.Equal(ShortcutMatchKind.Dictation, rightMatch);
        Assert.Equal(ShortcutMatchKind.None, leftMatch);
    }

    private static GlobalShortcutSet DefaultSet()
    {
        return new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            RecordingMode.Toggle,
            false
        );
    }
}