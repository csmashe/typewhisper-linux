using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
/// Enumerates keyboard-capable <c>/dev/input/eventN</c> nodes by probing each
/// with <c>ioctl(EVIOCGBIT)</c>. We can't rely on udev's
/// <c>by-path/*-event-kbd</c> symlinks: virtual keyboards created via uinput
/// (input remappers like kanata / keyd / xremap) never get those symlinks,
/// and when such a remapper grabs the physical keyboard and re-emits
/// keystrokes through its own virtual device, the global hotkey arrives on a
/// device a symlink-only scan would miss entirely.
///
/// TypeWhisper's own ydotool injection device is excluded by name —
/// <see cref="EvdevGlobalShortcutBackend"/> aggregates modifier state
/// globally across every watched device, so watching our own synthetic
/// output (e.g. Ctrl+V paste chords) could form phantom hotkey chords.
/// </summary>
internal static class KeyboardDeviceDiscovery
{
    private const string InputDir = "/dev/input";

    // input-event-codes.h — representative typing keys. A device that
    // declares all of these is a keyboard (physical or virtual); buttons,
    // switches, lid sensors and mouse-only devices declare none of them.
    private const int KeyEnter = 28;
    private const int KeyA = 30;
    private const int KeyZ = 44;
    private const int KeySpace = 57;

    // input-event-codes.h: KEY_MAX = 0x2ff. The EV_KEY capability bitmap is
    // (KEY_MAX / 8) + 1 = 96 bytes.
    private const int KeyMax = 0x2ff;
    private const int KeyBitmapBytes = (KeyMax / 8) + 1;
    private const int NameBufferBytes = 256;

    public static IReadOnlyList<string> EnumerateKeyboards()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(InputDir)) return result;
            foreach (var node in Directory.EnumerateFiles(InputDir, "event*"))
            {
                if (IsKeyboardNode(node))
                    result.Add(node);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[KeyboardDeviceDiscovery] Enumerate threw: {ex.Message}");
        }

        // Stable order by numeric event index — keeps attach logs readable.
        result.Sort(static (a, b) => EventIndex(a).CompareTo(EventIndex(b)));
        return result;
    }

    /// <summary>
    /// Opens <paramref name="node"/> and probes it: keep it when it is
    /// keyboard-capable and not TypeWhisper's own ydotool output device. A
    /// node we can't open (permissions, already removed) is treated as
    /// not-a-keyboard — we could not watch it anyway.
    /// </summary>
    private static bool IsKeyboardNode(string node)
    {
        try
        {
            using var stream = new FileStream(
                node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var handle = stream.SafeFileHandle;

            var keyBits = new byte[KeyBitmapBytes];
            if (ioctl(handle, EviocgBit(InputEvent.EV_KEY, KeyBitmapBytes), keyBits) < 0)
                return false;
            if (!LooksLikeKeyboard(keyBits))
                return false;

            var nameBuf = new byte[NameBufferBytes];
            var nameLen = ioctl(handle, EviocgName(NameBufferBytes), nameBuf);
            var name = nameLen > 0 ? DecodeCString(nameBuf, nameLen) : string.Empty;
            return !IsExcludedByName(name);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[KeyboardDeviceDiscovery] Probe {node} skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the EV_KEY capability bitmap declares the representative
    /// typing keys — the signal that the device is a keyboard (physical or
    /// virtual) rather than a button, switch, lid sensor or mouse.
    /// </summary>
    internal static bool LooksLikeKeyboard(ReadOnlySpan<byte> evKeyBits)
        => IsBitSet(evKeyBits, KeyEnter)
           && IsBitSet(evKeyBits, KeyA)
           && IsBitSet(evKeyBits, KeyZ)
           && IsBitSet(evKeyBits, KeySpace);

    /// <summary>
    /// True for TypeWhisper's own ydotool injection device. Its synthetic
    /// keystrokes (including Ctrl+V paste chords) must not be watched:
    /// <see cref="EvdevGlobalShortcutBackend"/> aggregates modifier state
    /// globally, so our own output could otherwise form phantom hotkey
    /// chords with a real keypress on the physical keyboard.
    /// </summary>
    internal static bool IsExcludedByName(string deviceName)
        => deviceName.Contains("ydotoold", StringComparison.OrdinalIgnoreCase);

    private static bool IsBitSet(ReadOnlySpan<byte> bits, int bit)
    {
        var index = bit / 8;
        return index < bits.Length && (bits[index] & (1 << (bit % 8))) != 0;
    }

    /// <summary>Decodes a NUL-terminated C string from an ioctl buffer.</summary>
    private static string DecodeCString(byte[] buffer, int length)
    {
        var len = Math.Min(length, buffer.Length);
        var nul = Array.IndexOf(buffer, (byte)0, 0, len);
        if (nul >= 0) len = nul;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    /// <summary>Parses the N from a "/dev/input/eventN" path; unparseable sorts last.</summary>
    private static int EventIndex(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 5 && int.TryParse(name.AsSpan(5), out var n) ? n : int.MaxValue;
    }

    // --- evdev ioctl interop -------------------------------------------------
    // _IOC(dir,type,nr,size) = (dir<<30) | (size<<16) | (type<<8) | nr
    // _IOC_READ = 2; evdev ioctl type 'E' = 0x45.
    // EVIOCGBIT(ev,len): nr = 0x20 + ev.   EVIOCGNAME(len): nr = 0x06.
    private const uint IocRead = 2u;
    private const uint EvdevIocType = 0x45u;

    private static nuint EviocgBit(int ev, int len)
        => (nuint)((IocRead << 30) | ((uint)len << 16) | (EvdevIocType << 8) | (uint)(0x20 + ev));

    private static nuint EviocgName(int len)
        => (nuint)((IocRead << 30) | ((uint)len << 16) | (EvdevIocType << 8) | 0x06u);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(SafeFileHandle fd, nuint request, byte[] buf);
}
