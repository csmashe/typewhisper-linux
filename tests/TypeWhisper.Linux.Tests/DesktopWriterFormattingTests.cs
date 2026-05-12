using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Pure-function tests for the per-DE accelerator format converters.
/// These don't touch disk or shell out — fast and stable in CI.
/// </summary>
public sealed class DesktopWriterFormattingTests
{
    [Fact]
    public void Hyprland_ConvertsCtrlShiftSpace_ToCanonicalForm()
    {
        var (mods, key) = HyprlandShortcutWriter.ToHyprlandBind("Ctrl+Shift+Space");
        Assert.Equal("CTRL SHIFT", mods);
        Assert.Equal("SPACE", key);
    }

    [Fact]
    public void Hyprland_HandlesSingleModifier()
    {
        var (mods, key) = HyprlandShortcutWriter.ToHyprlandBind("Super+K");
        Assert.Equal("SUPER", mods);
        Assert.Equal("K", key);
    }

    [Fact]
    public void Sway_ConvertsCtrlShiftSpace_ToCanonicalForm()
    {
        Assert.Equal("Ctrl+Shift+space", SwayShortcutWriter.ToSwayBind("Ctrl+Shift+Space"));
    }

    [Fact]
    public void Sway_LowercasesNamedKeysButPreservesSingleChars()
    {
        Assert.Equal("Ctrl+space", SwayShortcutWriter.ToSwayBind("Ctrl+Space"));
        Assert.Equal("Alt+F9", SwayShortcutWriter.ToSwayBind("Alt+F9"));
    }

    [Fact]
    public void Sway_MapsSuperToMod4()
    {
        Assert.Equal("Mod4+k", SwayShortcutWriter.ToSwayBind("Super+k"));
    }
}
