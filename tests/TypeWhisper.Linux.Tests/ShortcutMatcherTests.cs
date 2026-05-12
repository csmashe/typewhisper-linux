using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutMatcherTests
{
    private static GlobalShortcutSet DefaultSet() => new(
        DictationKey: KeyCode.VcSpace,
        DictationModifiers: ModifierMask.LeftCtrl | ModifierMask.LeftShift,
        PromptPaletteKey: null,
        PromptPaletteModifiers: ModifierMask.None,
        RecentTranscriptionsKey: null,
        RecentTranscriptionsModifiers: ModifierMask.None,
        CopyLastTranscriptionKey: null,
        CopyLastTranscriptionModifiers: ModifierMask.None,
        TransformSelectionKey: null,
        TransformSelectionModifiers: ModifierMask.None,
        CancelKey: KeyCode.VcEscape,
        CancelModifiers: ModifierMask.None,
        Mode: RecordingMode.Toggle,
        IsCancelEnabled: false);

    [Fact]
    public void Match_DefaultBinding_IdentifiesDictation()
    {
        var kind = ShortcutMatcher.Match(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            DefaultSet());

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
    public void Match_RightCtrlSubstitutesForLeftCtrl()
    {
        // Live keyboards routinely report RightCtrl for chords pressed on the
        // right side; the matcher must treat the two as interchangeable so
        // configured Ctrl+Shift+Space still fires.
        var kind = ShortcutMatcher.Match(
            KeyCode.VcSpace,
            ModifierMask.RightCtrl | ModifierMask.RightShift,
            DefaultSet());

        Assert.Equal(ShortcutMatchKind.Dictation, kind);
    }
}
