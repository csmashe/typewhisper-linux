using Avalonia.Input;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecordedDesktopKeyTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"tw-desktop-keys-{Guid.NewGuid():N}");
    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }
    public static IEnumerable<object[]> NamedKeys => new[]
    {
        new object[] { Key.Enter, "Enter", "Return", "Return" },
        new object[] { Key.Tab, "Tab", "Tab", "Tab" },
        new object[] { Key.Escape, "Escape", "Escape", "Esc" },
        new object[] { Key.Back, "Backspace", "BackSpace", "Backspace" },
        new object[] { Key.Delete, "Delete", "Delete", "Del" },
        new object[] { Key.Home, "Home", "Home", "Home" },
        new object[] { Key.End, "End", "End", "End" },
        new object[] { Key.PageUp, "PageUp", "Page_Up", "PgUp" },
        new object[] { Key.PageDown, "PageDown", "Page_Down", "PgDown" },
        new object[] { Key.Left, "Left", "Left", "Left" },
        new object[] { Key.Right, "Right", "Right", "Right" },
        new object[] { Key.Up, "Up", "Up", "Up" },
        new object[] { Key.Down, "Down", "Down", "Down" },
        new object[] { Key.Space, "Space", "space", "Space" },
    };

    [Theory]
    [MemberData(nameof(NamedKeys))]
    public void RecordedNamedKeyUsesDesktopSpelling(Key key, string name, string keysym, string qtName)
    {
        var recorder = new HotkeyChordRecorder();
        recorder.Handle(key, KeyModifiers.Control, true);
        var chord = recorder.Handle(key, KeyModifiers.None, false).Chord;
        Assert.Equal($"Ctrl+{name}", chord);
        Assert.Equal($"<Control>{keysym}", GnomeShortcutWriter.FormatGnomeAccel(chord!));
        Assert.Equal($"Ctrl+{keysym}", SwayShortcutWriter.ToSwayBind(chord!));
        Assert.Equal(("CTRL", keysym), HyprlandShortcutWriter.ToHyprlandBind(chord!));
        var preview = new KdeShortcutWriter().PreviewLines(new("test", "Test", chord!, "typewhisper", null, null, null));
        Assert.Contains($"X-KDE-Shortcuts=Ctrl+{qtName}\n", preview);
    }

    [Fact]
    public void EveryRecordedKeyHasAValidDesktopRouteOrAnExplicitModifierRestriction()
    {
        var named = NamedKeys.ToDictionary(row => (Key)row[0], row => (string)row[2]);
        foreach (var key in Enum.GetValues<Key>().Distinct())
        {
            var recorder = new HotkeyChordRecorder();
            recorder.Handle(key, KeyModifiers.Control, true);
            var chord = recorder.Handle(key, KeyModifiers.None, false).Chord;
            if (chord is null) continue;
            if (!chord.StartsWith("Ctrl+", StringComparison.Ordinal))
            {
                var keysym = chord switch
                {
                    "Left Ctrl" => "Control_L", "Right Ctrl" => "Control_R",
                    "Left Shift" => "Shift_L", "Right Shift" => "Shift_R",
                    "Left Alt" => "Alt_L", "Right Alt" => "Alt_R",
                    "Left Meta" => "Super_L", "Right Meta" => "Super_R",
                    _ => throw new InvalidOperationException(chord),
                };
                Assert.Equal(keysym, SwayShortcutWriter.ToSwayBind(chord));
                Assert.Equal(("", keysym), HyprlandShortcutWriter.ToHyprlandBind(chord));
                var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
                settings.Save(settings.Current with { ToggleHotkey = chord, Mode = RecordingMode.Toggle });
                Assert.Null(DictationShortcutSpecFactory.Build(settings, new GnomeShortcutWriter()));
                Assert.Null(DictationShortcutSpecFactory.Build(settings, new KdeShortcutWriter()));
                continue;
            }
            var terminal = chord[5..];
            var expected = named.TryGetValue(key, out var n) ? n : terminal.Length > 1 && terminal.StartsWith('F') ? terminal : terminal.ToLowerInvariant();
            Assert.Equal($"<Control>{expected}", GnomeShortcutWriter.FormatGnomeAccel(chord));
            Assert.Equal($"Ctrl+{expected}", SwayShortcutWriter.ToSwayBind(chord));
            Assert.Equal(("CTRL", expected), HyprlandShortcutWriter.ToHyprlandBind(chord));
        }
    }
}
