using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Highest-risk surface in Phase 6 per the phase spec: the GNOME
/// custom-keybindings list parser. A bug here can silently overwrite
/// the user's other custom shortcuts. The tests cover every shape
/// gsettings is documented (or observed in the wild) to emit, plus a
/// few hand-edited-via-dconf-editor variants.
/// </summary>
public sealed class GnomeShortcutWriterTests
{
    [Fact]
    public void ParseGSettingsList_Empty_TypedAnnotation_ReturnsEmptyList()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("@as []");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseGSettingsList_Empty_BareBrackets_ReturnsEmptyList()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("[]");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseGSettingsList_SingleEntry_ReturnsOneItem()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList(
            "['/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/']");
        Assert.Single(result);
        Assert.Equal("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/", result[0]);
    }

    [Fact]
    public void ParseGSettingsList_MultipleEntries_ReturnsAll()
    {
        var raw = "['/org/.../custom0/', '/org/.../custom1/', '/org/.../custom2/']";
        var result = GnomeShortcutWriter.ParseGSettingsList(raw);
        Assert.Equal(3, result.Count);
        Assert.Equal("/org/.../custom0/", result[0]);
        Assert.Equal("/org/.../custom1/", result[1]);
        Assert.Equal("/org/.../custom2/", result[2]);
    }

    [Fact]
    public void ParseGSettingsList_HandlesTrailingNewline()
    {
        // gsettings always appends "\n" — parser must tolerate it.
        var result = GnomeShortcutWriter.ParseGSettingsList("['/a/', '/b/']\n");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseGSettingsList_HandlesEscapedSingleQuote()
    {
        // A user-renamed entry could contain an apostrophe — gsettings
        // escapes it as \'.
        var result = GnomeShortcutWriter.ParseGSettingsList(@"['Chris\'s key', '/b/']");
        Assert.Equal(2, result.Count);
        Assert.Equal("Chris's key", result[0]);
    }

    [Fact]
    public void ParseGSettingsList_HandlesDoubleQuotedEntries()
    {
        // Hand-edited via dconf-editor — double quotes are accepted.
        var result = GnomeShortcutWriter.ParseGSettingsList("[\"/a/\", \"/b/\"]");
        Assert.Equal(2, result.Count);
        Assert.Equal("/a/", result[0]);
        Assert.Equal("/b/", result[1]);
    }

    [Fact]
    public void ParseGSettingsList_TolerantOfExtraWhitespace()
    {
        var result = GnomeShortcutWriter.ParseGSettingsList("  [  '/a/' ,  '/b/'  ]  ");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseGSettingsList_RejectsMalformedShape()
    {
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("notalist"));
    }

    [Fact]
    public void ParseGSettingsList_RejectsBlankInput_FailClosed()
    {
        // Critical data-loss guard: an empty stdout from gsettings is
        // anomalous, not an empty list. Treating it as empty would let
        // us overwrite real user shortcuts on the very next set.
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList(""));
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("   "));
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("\n"));
    }

    [Fact]
    public void ParseGSettingsList_RejectsUnknownEscape()
    {
        // \n inside a single-quoted entry is not something gsettings
        // emits — accepting it would let us silently rewrite the
        // user's entry on round-trip.
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList(@"['foo\nbar']"));
    }

    [Fact]
    public void ParseGSettingsList_RejectsUnterminatedQuote()
    {
        // Critical: refuse rather than silently dropping the entry,
        // because returning an incomplete list would cause the writer
        // to wipe out a real entry on round-trip.
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseGSettingsList("['/a/"));
    }

    [Fact]
    public void FormatGSettingsList_Empty_ReturnsBareBrackets()
    {
        Assert.Equal("[]", GnomeShortcutWriter.FormatGSettingsList(Array.Empty<string>()));
    }

    [Fact]
    public void FormatGSettingsList_SingleEntry_RoundTripsThroughParser()
    {
        var input = new[] { "/org/.../typewhisper-abcd1234/" };
        var formatted = GnomeShortcutWriter.FormatGSettingsList(input);
        var parsed = GnomeShortcutWriter.ParseGSettingsList(formatted);
        Assert.Equal(input, parsed);
    }

    [Fact]
    public void FormatGSettingsList_EscapesSingleQuotesAndBackslashes()
    {
        var input = new[] { "Chris's key", @"path\with\backslash" };
        var formatted = GnomeShortcutWriter.FormatGSettingsList(input);
        var parsed = GnomeShortcutWriter.ParseGSettingsList(formatted);
        Assert.Equal(input, parsed);
    }

    [Fact]
    public void FormatGnomeAccel_ProducesGtkAcceleratorFormat()
    {
        Assert.Equal("<Control><Shift>space", GnomeShortcutWriter.FormatGnomeAccel("Ctrl+Shift+Space"));
        Assert.Equal("<Control><Alt>F9", GnomeShortcutWriter.FormatGnomeAccel("Ctrl+Alt+F9"));
        Assert.Equal("<Super>k", GnomeShortcutWriter.FormatGnomeAccel("Super+K"));
    }

    [Fact]
    public void FormatGnomeAccel_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GnomeShortcutWriter.FormatGnomeAccel(""));
    }
}
