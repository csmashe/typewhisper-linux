using System.Windows.Input;
using TypeWhisper.Windows.Controls;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.PluginSystem.Tests;

public class HotkeyInputTests
{
    [Fact]
    public void ModifierOnly_WinAlt_PressWinThenAlt_DoesNotSwallowAltKeyUp()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var altDown = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);
        Assert.True(altDown.RaiseKeyDown);
        Assert.True(altDown.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.False(winUp.RaiseKeyUp);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_WinAlt_ReleaseWinFirst_SwallowsOnlyWinKeyUp()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.RaiseKeyUp);
        Assert.True(winUp.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, altUp);
    }

    [Fact]
    public void ModifierOnly_WinCtrlAlt_ActivatesInArbitraryOrder()
    {
        var sut = CreateStateMachine(
            NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var altDown = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);
        Assert.True(altDown.RaiseKeyDown);
        Assert.True(altDown.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedFirst_RequestsSyntheticWinRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var ctrlDown = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.True(ctrlDown.RaiseKeyDown);
        Assert.True(ctrlDown.Swallow);
        Assert.Equal(0u, ctrlDown.SyntheticKeyUpVk);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.True(winUp.RaiseKeyUp);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedAlone_ReplaysStandaloneWinTap()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, winUp.SyntheticKeyTapVk);
        Assert.False(winUp.RaiseKeyUp);
    }

    [Fact]
    public void KeyedHotkey_WinCtrlX_SwallowsTriggerAndWinRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL, (uint)'X');

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var xDown = sut.ProcessKeyEvent((uint)'X', isKeyDown: true, isKeyUp: false);
        Assert.True(xDown.RaiseKeyDown);
        Assert.True(xDown.Swallow);

        var xUp = sut.ProcessKeyEvent((uint)'X', isKeyDown: false, isKeyUp: true);
        Assert.True(xUp.RaiseKeyUp);
        Assert.True(xUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void RecorderSession_TracksWinModifierWithoutImplicitAlt()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);
        sut.NoteModifierDown(Key.LeftCtrl);

        Assert.Equal(ModifierKeys.Windows | ModifierKeys.Control, sut.GetCurrentModifiers());
        Assert.Equal("Ctrl+Win+K", sut.TryRecordHotkey(Key.K));
    }

    [Fact]
    public void RecorderSession_RecordsModifierOnlyWinAltCombo()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);
        sut.NoteModifierDown(Key.LeftAlt);

        var hotkey = sut.TryRecordModifierOnlyOnRelease(Key.LeftAlt);

        Assert.Equal("Alt+Win", hotkey);
        Assert.Equal(ModifierKeys.Windows, sut.GetCurrentModifiers());
    }

    [Fact]
    public void RecorderSession_DoesNotRecordSingleModifierRelease()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);

        var hotkey = sut.TryRecordModifierOnlyOnRelease(Key.LWin);

        Assert.Equal("", hotkey);
        Assert.Equal(ModifierKeys.None, sut.GetCurrentModifiers());
    }

    private static HotkeyMatchStateMachine CreateStateMachine(uint modifiers, uint vk = 0)
    {
        var sut = new HotkeyMatchStateMachine();
        sut.SetHotkey(modifiers, vk);
        return sut;
    }
}
