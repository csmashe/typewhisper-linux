using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Enumerates keyboard-capable <c>/dev/input/eventN</c> nodes from their
///     sysfs EV_KEY capability bitmap, falling back to <c>ioctl(EVIOCGBIT)</c>
///     when sysfs doesn't describe a node. Udev <c>by-path/*-event-kbd</c> symlinks are
///     not used because virtual keyboards created by input remappers (kanata,
///     keyd, xremap) never get them — hotkeys would be missed. TypeWhisper's own
///     ydotool device is excluded by name to prevent synthetic Ctrl+V chords from
///     forming phantom hotkeys against real keypresses.
/// </summary>
internal static partial class KeyboardDeviceDiscovery
{
    private const string InputDir = "/dev/input";
    private const string SysClassInputDir = "/sys/class/input";

    // unistd.h: access(2) mode for read permission.
    private const int ReadOk = 4;

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

            result.AddRange(
                Directory.EnumerateFiles(InputDir, "event*")
                    .Where(node => TryClassifyFromSysfs(node, out var isKeyboard)
                        ? isKeyboard
                        : IsKeyboardNode(node)));
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
    ///     Classifies the node from sysfs without opening it: closing an evdev fd waits
    ///     on an RCU grace period (~5-60 ms per node, ~0.5 s per full sweep). False when
    ///     sysfs doesn't describe the node, so the caller falls back to the ioctl probe.
    /// </summary>
    private static bool TryClassifyFromSysfs(string node, out bool isKeyboard)
    {
        isKeyboard = false;
        try
        {
            var device = Path.Join(SysClassInputDir, Path.GetFileName(node), "device");
            var keyBits = ParseSysfsBitmap(
                File.ReadAllText(Path.Join(device, "capabilities", "key")),
                KeyBitmapBytes,
                Environment.Is64BitOperatingSystem ? 64 : 32
            );
            if (keyBits is null)
            {
                return false;
            }

            var name = File.ReadAllText(Path.Join(device, "name")).TrimEnd('\n');
            isKeyboard = LooksLikeKeyboard(keyBits)
                         && !IsExcludedByName(name)
                         && access(node, ReadOk) == 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Converts a sysfs capability bitmap ("hex words, most significant
    ///     first, each one kernel <c>long</c>") into the little-endian byte layout
    ///     <c>EVIOCGBIT</c> returns. Null when the text is malformed.
    /// </summary>
    internal static byte[]? ParseSysfsBitmap(string text, int byteCount, int wordBits)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return null;
        }

        var bytes = new byte[byteCount];
        var wordBytes = wordBits / 8;
        for (var w = 0; w < words.Length; w++)
        {
            if (!ulong.TryParse(words[^(w + 1)], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            for (var b = 0; b < wordBytes; b++)
            {
                var index = w * wordBytes + b;
                if (index < byteCount)
                {
                    bytes[index] = (byte)(value >> (8 * b));
                }
            }
        }

        return bytes;
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

    // byte[] is blittable, so the source-generated marshaller pins it (same as DllImport); [Out] makes
    // explicit that ioctl writes the device-name bytes back into buf.
    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(SafeFileHandle fd, nuint request, [Out] byte[] buf);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string pathname, int mode);
}