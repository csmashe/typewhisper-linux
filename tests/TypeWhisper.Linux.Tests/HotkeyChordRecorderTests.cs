using Avalonia.Input;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HotkeyChordRecorderTests
{
    [Fact]
    public void EscapeCancels()
    {
        var recorder = new HotkeyChordRecorder();
        Assert.True(recorder.Handle(Key.Escape, KeyModifiers.None, true).Cancelled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChordWaitsForEveryKeyRelease(bool modifierFirst)
    {
        var recorder = new HotkeyChordRecorder();
        recorder.Handle(Key.LeftCtrl, KeyModifiers.Control, true);
        recorder.Handle(Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift, true);
        Assert.Null(recorder.Handle(Key.K, KeyModifiers.Control | KeyModifiers.Shift, true).Chord);
        var order = modifierFirst ? new[] { Key.LeftCtrl, Key.LeftShift, Key.K } : new[] { Key.K, Key.LeftCtrl, Key.LeftShift };
        Assert.Null(recorder.Handle(order[0], KeyModifiers.Control | KeyModifiers.Shift, false).Chord);
        Assert.Null(recorder.Handle(order[1], KeyModifiers.Shift, false).Chord);
        Assert.Equal("Ctrl+Shift+K", recorder.Handle(order[2], KeyModifiers.None, false).Chord);
    }

    [Theory]
    [InlineData(Key.LeftCtrl, "Left Ctrl")]
    [InlineData(Key.RightCtrl, "Right Ctrl")]
    [InlineData(Key.LeftShift, "Left Shift")]
    [InlineData(Key.RightShift, "Right Shift")]
    [InlineData(Key.LeftAlt, "Left Alt")]
    [InlineData(Key.RightAlt, "Right Alt")]
    [InlineData(Key.LWin, "Left Meta")]
    [InlineData(Key.RWin, "Right Meta")]
    public void BareModifierWaitsForRelease(Key key, string expected)
    {
        var recorder = new HotkeyChordRecorder();
        Assert.Null(recorder.Handle(key, KeyModifiers.None, true).Chord);
        Assert.Equal(expected, recorder.Handle(key, KeyModifiers.None, false).Chord);
    }

    [Theory]
    [InlineData(Key.LeftCtrl, Key.LeftShift)]
    [InlineData(Key.LeftShift, Key.LeftCtrl)]
    public void MultipleModifiersNeverProduceABareModifier(Key firstRelease, Key lastRelease)
    {
        var recorder = new HotkeyChordRecorder();
        recorder.Handle(Key.LeftCtrl, KeyModifiers.Control, true);
        recorder.Handle(Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift, true);
        Assert.Null(recorder.Handle(firstRelease, KeyModifiers.Shift, false).Chord);
        Assert.Null(recorder.Handle(lastRelease, KeyModifiers.None, false).Chord);
    }

    [Fact]
    public void EveryEmittedNameRoundTripsAndUnsupportedKeysAreRejected()
    {
        foreach (var key in Enum.GetValues<Key>().Distinct())
        {
            var recorder = new HotkeyChordRecorder();
            var down = recorder.Handle(key, KeyModifiers.None, true);
            var up = recorder.Handle(key, KeyModifiers.None, false);
            if (up.Chord is not { } chord) continue;
            using var hotkey = TestShortcutBackend.CreateHotkeyService();
            Assert.True(hotkey.TrySetHotkeyFromString(chord), chord);
            Assert.Equal(chord, hotkey.CurrentHotkeyString);
        }
        foreach (var key in new[] { Key.PrintScreen, Key.Pause, Key.CapsLock, Key.Scroll, Key.Apps, Key.Oem8, Key.OemTilde, Key.NumPad1 })
        {
            Assert.True(new HotkeyChordRecorder().Handle(key, KeyModifiers.Control, true).Unsupported);
        }
        using var service = TestShortcutBackend.CreateHotkeyService();
        foreach (var text in new[] { "Ctrl+PrintScreen", "Ctrl+Pause", "Ctrl+CapsLock", "Ctrl+LeftShift", "Ctrl+BackQuote", "Ctrl+NumPad1" })
            Assert.False(service.TrySetHotkeyFromString(text), text);
    }
}
