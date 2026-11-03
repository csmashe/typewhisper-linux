using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the pure classifier surface of <see cref="KeyboardDeviceDiscovery" />
///     — <c>LooksLikeKeyboard</c> (EV_KEY-bitmap classification) and
///     <c>IsExcludedByName</c>. Device enumeration itself is <c>/dev/input</c>
///     I/O and is verified manually. Bit values are from Linux
///     <c>input-event-codes.h</c>.
/// </summary>
public sealed class KeyboardDeviceDiscoveryTests
{
    private const int KeyEnter = 28;
    private const int KeyA = 30;
    private const int KeyZ = 44;
    private const int KeySpace = 57;
    private const int KeyPower = 116;
    private const int BtnLeft = 0x110;

    [Fact]
    public void LooksLikeKeyboard_true_for_a_device_with_the_typing_keys()
    {
        Assert.True(
            KeyboardDeviceDiscovery.LooksLikeKeyboard(Bitmap(KeyEnter, KeyA, KeyZ, KeySpace))
        );
    }

    [Fact]
    public void LooksLikeKeyboard_false_for_an_empty_bitmap()
    {
        Assert.False(KeyboardDeviceDiscovery.LooksLikeKeyboard(new byte[96]));
    }

    [Fact]
    public void LooksLikeKeyboard_false_for_a_mouse_only_device()
        // A device declaring only mouse buttons (BTN_LEFT) is not a keyboard.
    {
        Assert.False(KeyboardDeviceDiscovery.LooksLikeKeyboard(Bitmap(BtnLeft)));
    }

    [Fact]
    public void LooksLikeKeyboard_false_for_a_power_button()
        // ACPI power button: declares KEY_POWER but none of the typing keys.
    {
        Assert.False(KeyboardDeviceDiscovery.LooksLikeKeyboard(Bitmap(KeyPower)));
    }

    [Fact]
    public void LooksLikeKeyboard_false_when_a_typing_key_is_missing()
        // Missing KEY_SPACE — the full representative set is required.
    {
        Assert.False(KeyboardDeviceDiscovery.LooksLikeKeyboard(Bitmap(KeyEnter, KeyA, KeyZ)));
    }

    [Theory]
    [InlineData("ydotoold virtual device", true)]
    [InlineData("YDOTOOLD virtual device", true)]
    [InlineData("DYGMA RAISE2 Keyboard", false)]
    [InlineData("vicinae-snippet-virtual-keyboard", false)]
    [InlineData("", false)]
    public void IsExcludedByName_only_excludes_the_ydotool_device(string name, bool excluded)
    {
        Assert.Equal(excluded, KeyboardDeviceDiscovery.IsExcludedByName(name));
    }

    [Fact]
    public void ParseSysfsBitmap_reads_a_real_keyboard_as_a_keyboard()
        // DYGMA RAISE2 Keyboard's /sys/class/input/eventN/device/capabilities/key.
    {
        var bits = KeyboardDeviceDiscovery.ParseSysfsBitmap(
            "3f00733fff 0 0 483ffff17aff32d bfd4444600000000 1 130ff38b17d007 "
            + "ffff7bfad941dfff ffbeffdfffefffff fffffffffffffffe\n",
            96,
            64
        );

        Assert.NotNull(bits);
        Assert.True(KeyboardDeviceDiscovery.LooksLikeKeyboard(bits));
    }

    [Fact]
    public void ParseSysfsBitmap_reads_an_empty_capability_as_not_a_keyboard()
    {
        var bits = KeyboardDeviceDiscovery.ParseSysfsBitmap("0\n", 96, 64);

        Assert.NotNull(bits);
        Assert.False(KeyboardDeviceDiscovery.LooksLikeKeyboard(bits));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(32)]
    public void ParseSysfsBitmap_places_the_last_word_at_bit_zero(int wordBits)
    {
        // Two words: the trailing one holds bits 0..wordBits-1, the leading one the next word.
        var bits = KeyboardDeviceDiscovery.ParseSysfsBitmap("1 4", 96, wordBits);

        Assert.NotNull(bits);
        Assert.Equal(Bitmap(2, wordBits), bits);
    }

    [Fact]
    public void ParseSysfsBitmap_ignores_words_beyond_the_buffer()
    {
        var bits = KeyboardDeviceDiscovery.ParseSysfsBitmap("ff 0 1", 8, 64);

        Assert.NotNull(bits);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, bits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("zz 1")]
    public void ParseSysfsBitmap_rejects_malformed_text(string text)
    {
        Assert.Null(KeyboardDeviceDiscovery.ParseSysfsBitmap(text, 96, 64));
    }

    /// <summary>Builds a 96-byte EV_KEY bitmap with the given key bits set.</summary>
    private static byte[] Bitmap(params int[] setBits)
    {
        var bytes = new byte[96]; // (KEY_MAX 0x2ff / 8) + 1
        foreach (var bit in setBits)
        {
            bytes[bit / 8] |= (byte)(1 << (bit % 8));
        }

        return bytes;
    }
}