using SharpHook.Native;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxKeyMapTests
{
    [Theory]
    [InlineData(57, KeyCode.VcSpace)] // KEY_SPACE
    [InlineData(1, KeyCode.VcEscape)] // KEY_ESC
    [InlineData(28, KeyCode.VcEnter)] // KEY_ENTER
    [InlineData(15, KeyCode.VcTab)] // KEY_TAB
    [InlineData(30, KeyCode.VcA)] // KEY_A
    [InlineData(50, KeyCode.VcM)] // KEY_M
    [InlineData(59, KeyCode.VcF1)]
    [InlineData(88, KeyCode.VcF12)]
    [InlineData(2, KeyCode.Vc1)]
    [InlineData(11, KeyCode.Vc0)]
    public void ToSharpHook_MapsCommonChordKeys(int linuxCode, KeyCode expected)
    {
        Assert.Equal(expected, LinuxKeyMap.ToSharpHook(linuxCode));
    }

    [Fact]
    public void ToSharpHook_UnknownCode_ReturnsNull()
    {
        Assert.Null(LinuxKeyMap.ToSharpHook(99999));
    }

    [Theory]
    [InlineData(LinuxKeyMap.KeyLeftctrl, ModifierMask.LeftCtrl)]
    [InlineData(LinuxKeyMap.KeyRightctrl, ModifierMask.RightCtrl)]
    [InlineData(LinuxKeyMap.KeyLeftshift, ModifierMask.LeftShift)]
    [InlineData(LinuxKeyMap.KeyRightshift, ModifierMask.RightShift)]
    [InlineData(LinuxKeyMap.KeyLeftalt, ModifierMask.LeftAlt)]
    [InlineData(LinuxKeyMap.KeyRightalt, ModifierMask.RightAlt)]
    [InlineData(LinuxKeyMap.KeyLeftmeta, ModifierMask.LeftMeta)]
    [InlineData(LinuxKeyMap.KeyRightmeta, ModifierMask.RightMeta)]
    public void ToModifier_MapsAllEightModifierKeys(int linuxCode, ModifierMask expected)
    {
        Assert.Equal(expected, LinuxKeyMap.ToModifier(linuxCode));
    }

    [Fact]
    public void ToModifier_NonModifier_ReturnsNone()
    {
        Assert.Equal(ModifierMask.None, LinuxKeyMap.ToModifier(57));
    }
}