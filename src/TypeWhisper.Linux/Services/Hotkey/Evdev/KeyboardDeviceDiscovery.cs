using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Enumerates keyboard-capable <c>/dev/input/eventN</c> nodes via
///     <c>ioctl(EVIOCGBIT)</c>. Udev <c>by-path/*-event-kbd</c> symlinks are
///     not used because virtual keyboards created by input remappers (kanata,
///     keyd, xremap) never get them — hotkeys would be missed. TypeWhisper's own
///     ydotool device is excluded by name to prevent synthetic Ctrl+V chords from
///     forming phantom hotkeys against real keypresses.
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
    private const int KeyBitmapBytes = KeyMax / 8 + 1;
    private const int NameBufferBytes = 256;

    // --- evdev ioctl interop -------------------------------------------------
    // _IOC(dir,type,nr,size) = (dir<<30) | (size<<16) | (type<<8) | nr
    // _IOC_READ = 2; evdev ioctl type 'E' = 0x45.
    // EVIOCGBIT(ev,len): nr = 0x20 + ev.   EVIOCGNAME(len): nr = 0x06.
    private const uint IocRead = 2u;
    private const uint EvdevIocType = 0x45u;

    public static IReadOnlyList<string> EnumerateKeyboards()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(InputDir))
            {
                return result;
            }

            foreach (var node in Directory.EnumerateFiles(InputDir, "event*"))
            {
                if (IsKeyboardNode(node))
                {
                    result.Add(node);
                }
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
    ///     True when the EV_KEY capability bitmap includes representative typing
    ///     keys, distinguishing keyboards from buttons, switches, and mice.
    /// </summary>
    internal static bool LooksLikeKeyboard(ReadOnlySpan<byte> evKeyBits)
    {
        return IsBitSet(evKeyBits, KeyEnter)
               && IsBitSet(evKeyBits, KeyA)
               && IsBitSet(evKeyBits, KeyZ)
               && IsBitSet(evKeyBits, KeySpace);
    }

    /// <summary>
    ///     True for TypeWhisper's own ydotool injection device, which must not be
    ///     watched — its synthetic Ctrl+V chords could form phantom hotkey matches
    ///     against real keypresses via the global modifier-state aggregation.
    /// </summary>
    internal static bool IsExcludedByName(string deviceName)
    {
        return deviceName.Contains("ydotoold", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Opens the node, checks capability bits and device name. Nodes that
    ///     can't be opened (permissions, removed) are treated as non-keyboards.
    /// </summary>
    private static bool IsKeyboardNode(string node)
    {
        try
        {
            using var stream = new FileStream(
                node,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var handle = stream.SafeFileHandle;

            var keyBits = new byte[KeyBitmapBytes];
            if (ioctl(handle, EviocgBit(InputEvent.EvKey, KeyBitmapBytes), keyBits) < 0)
            {
                return false;
            }

            if (!LooksLikeKeyboard(keyBits))
            {
                return false;
            }

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
        if (nul >= 0)
        {
            len = nul;
        }

        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    /// <summary>Parses the N from a "/dev/input/eventN" path; unparseable sorts last.</summary>
    private static int EventIndex(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 5 && int.TryParse(name.AsSpan(5), out var n) ? n : int.MaxValue;
    }

    private static nuint EviocgBit(int ev, int len)
    {
        return (IocRead << 30) | ((uint)len << 16) | (EvdevIocType << 8) | (uint)(0x20 + ev);
    }

    private static nuint EviocgName(int len)
    {
        return (IocRead << 30) | ((uint)len << 16) | (EvdevIocType << 8) | 0x06u;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(SafeFileHandle fd, nuint request, byte[] buf);
}