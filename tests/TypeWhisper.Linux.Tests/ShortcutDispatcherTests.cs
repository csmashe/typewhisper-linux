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