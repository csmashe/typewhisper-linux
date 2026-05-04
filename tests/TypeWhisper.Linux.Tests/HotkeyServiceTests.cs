using SharpHook.Native;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void TrySetHotkeyFromString_ParsesModifiersAndKeys()
    {
        var hotkey = new HotkeyService();

        var parsed = hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        Assert.True(parsed);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public void TrySetPromptPaletteHotkeyFromString_RejectsInvalidBinding()
    {
        var hotkey = new HotkeyService();
        hotkey.SetPromptPaletteHotkey(KeyCode.VcP, ModifierMask.LeftCtrl);

        var parsed = hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Nope");

        Assert.False(parsed);
        Assert.Equal("Ctrl+P", hotkey.CurrentPromptPaletteHotkeyString);
    }

    [Fact]
    public void ModifiersMatch_TreatsRightCtrlAsEquivalentToLeftCtrl()
    {
        var matches = HotkeyService.ModifiersMatch(ModifierMask.RightCtrl, ModifierMask.LeftCtrl);

        Assert.True(matches);
    }
}
