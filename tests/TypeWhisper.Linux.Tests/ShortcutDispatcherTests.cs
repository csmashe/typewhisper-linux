using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutDispatcherTests
{
    private static GlobalShortcutSet Set(RecordingMode mode, bool cancelEnabled = false)
    {
        return new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            KeyCode.VcP,
            ModifierMask.LeftCtrl,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            mode,
            cancelEnabled
        );
    }

    private static GlobalShortcutSet SetWithPromptAction(
        string actionId,
        KeyCode key,
        ModifierMask mods
    )
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
            false,
            new[] { new PromptActionHotkey(actionId, key, mods) }
        );
    }

    [Fact]
    public void PromptActionPress_FiresPromptActionRequestedWithId()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        string? observed = null;
        int count = 0;
        d.PromptActionRequested += id =>
        {
            observed = id;
            count++;
        };

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);

        Assert.Equal("alpha", observed);
        Assert.Equal(1, count);
    }

    [Fact]
    public void PromptActionRelease_ClearsDedupEvenAfterShortcutsReplaced()
    {
        // Scenario: user holds Ctrl+Alt+R (alpha fires once). Mid-hold,
        // the prompt-action list is replaced (action deleted, rebound,
        // etc.). The release must still clear the press-time entry so a
        // subsequent press of the same physical key fires again instead
        // of being dedup'd against a ghost.
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        int count = 0;
        d.PromptActionRequested += _ => count++;

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);

        // Replace shortcut set mid-hold — alpha is gone, beta is now on R.
        d.UpdateShortcuts(
            SetWithPromptAction(
                "beta",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );

        d.Handle(KeyCode.VcR, ModifierMask.None, false);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);

        // Two distinct presses (alpha then beta) must both fire — the
        // dedup dictionary must have cleared on release of the physical
        // key despite the set no longer naming the original action.
        Assert.Equal(2, count);
    }

    [Fact]
    public void PromptActionRepeatedPress_DedupsUntilRelease()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        int count = 0;
        d.PromptActionRequested += _ => count++;

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.None, false);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);

        Assert.Equal(2, count);
    }

    [Fact]
    public void TogglePress_FiresToggle_NotStart()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        int toggle = 0,
            start = 0;
        d.DictationToggleRequested += () => toggle++;
        d.DictationStartRequested += () => start++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(1, toggle);
        Assert.Equal(0, start);
    }

    [Fact]
    public void PushToTalkPressAndRelease_FiresStartThenStop()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.PushToTalk));
        int start = 0,
            stop = 0;
        d.DictationStartRequested += () => start++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(1, start);
        Assert.Equal(1, stop);
    }

    [Fact]
    public void HybridShortPress_DoesNotFireStopOnRelease()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Hybrid));
        int toggle = 0,
            stop = 0;
        d.DictationToggleRequested += () => toggle++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(1, toggle);
        Assert.Equal(0, stop);
    }

    [Fact]
    public void OsAutoRepeat_DoesNotDoubleFire()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var toggle = 0;
        d.DictationToggleRequested += () => toggle++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(1, toggle);
    }

    [Fact]
    public void Escape_FiresCancelOnlyWhenEnabled()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var cancel = 0;
        d.CancelRequested += () => cancel++;

        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);
        Assert.Equal(0, cancel);

        d.Handle(KeyCode.VcEscape, ModifierMask.None, false);
        d.UpdateShortcuts(Set(RecordingMode.Toggle, true));
        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);
        Assert.Equal(1, cancel);
    }

    [Fact]
    public void PalettePress_FiresPalette()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var palette = 0;
        d.PromptPaletteRequested += () => palette++;

        d.Handle(KeyCode.VcP, ModifierMask.LeftCtrl, true);

        Assert.Equal(1, palette);
    }
}