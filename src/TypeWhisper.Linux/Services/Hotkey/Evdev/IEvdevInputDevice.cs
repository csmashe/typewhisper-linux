using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Native-I/O seam for one evdev node. Tests provide an in-memory implementation so reader
///     stream recovery never needs to open or inspect a real input device.
/// </summary>
internal interface IEvdevInputDevice : IDisposable
{
    void Open();

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    byte[] QueryPressedKeyBitmap();
}

/// <summary>File-backed Linux implementation of <see cref="IEvdevInputDevice" />.</summary>
internal sealed partial class EvdevInputDevice(string path) : IEvdevInputDevice
{
    // input-event-codes.h: KEY_MAX = 0x2ff, inclusive.
    internal const int KeyMax = 0x2ff;
    internal const int KeyBitmapBytes = KeyMax / 8 + 1;

    // _IOC_READ = 2; evdev ioctl type 'E' = 0x45; EVIOCGKEY request number = 0x18.
    private const uint IocRead = 2u;
    private const uint EvdevIocType = 0x45u;
    private const uint EviocgKeyNumber = 0x18u;

    private FileStream? _stream;

    public void Open()
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException($"Evdev device {path} is already open.");
        }

        _stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            0,
            true
        );
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var stream = _stream
                     ?? throw new InvalidOperationException($"Evdev device {path} is not open.");
        return stream.ReadAsync(buffer, ct);
    }

    public byte[] QueryPressedKeyBitmap()
    {
        var stream = _stream
                     ?? throw new InvalidOperationException($"Evdev device {path} is not open.");
        var keyBits = new byte[KeyBitmapBytes];
        if (ioctl(stream.SafeFileHandle, EviocgKey(KeyBitmapBytes), keyBits) < 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"EVIOCGKEY failed for {path}"
            );
        }

        return keyBits;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private static nuint EviocgKey(int len)
    {
        return (IocRead << 30)
               | ((uint)len << 16)
               | (EvdevIocType << 8)
               | EviocgKeyNumber;
    }

    // byte[] is blittable, so the source-generated marshaller pins the EV_KEY bitmap buffer.
    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(SafeFileHandle fd, nuint request, [Out] byte[] buf);
}
