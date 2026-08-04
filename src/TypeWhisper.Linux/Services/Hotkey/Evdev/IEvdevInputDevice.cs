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

    int Read(Span<byte> buffer, CancellationToken ct);

    void Wake();

    byte[] QueryPressedKeyBitmap();
}

/// <summary>File-backed Linux implementation of <see cref="IEvdevInputDevice" />.</summary>
internal sealed partial class EvdevInputDevice(string path) : IEvdevInputDevice
{
    // input-event-codes.h: KEY_MAX = 0x2ff, inclusive.
    internal const int KeyMax = 0x2ff;
    internal const int KeyBitmapBytes = KeyMax / 8 + 1;

    private const int ErrorBadFileDescriptor = 9;
    private const int ErrorInterrupted = 4;
    private const int ErrorTryAgain = 11;

    private const int EventFdCloseOnExec = 0x80000;
    private const int EventFdNonBlock = 0x800;

    private const int OpenReadOnly = 0;
    private const int OpenNoControllingTerminal = 0x100;
    private const int OpenNonBlock = 0x800;
    private const int OpenCloseOnExec = 0x80000;

    private const short PollIn = 0x001;
    private const short PollError = 0x008;
    private const short PollHangUp = 0x010;
    private const short PollInvalid = 0x020;

    // _IOC_READ = 2; evdev ioctl type 'E' = 0x45; EVIOCGKEY request number = 0x18.
    private const uint IocRead = 2u;
    private const uint EvdevIocType = 0x45u;
    private const uint EviocgKeyNumber = 0x18u;

    private readonly Lock _handleLock = new();
    private SafeFileHandle? _deviceHandle;
    private SafeFileHandle? _wakeHandle;

    public void Open()
    {
        lock (_handleLock)
        {
            if (_deviceHandle is not null || _wakeHandle is not null)
            {
                throw new InvalidOperationException($"Evdev device {path} is already open.");
            }

            SafeFileHandle? wakeHandle = null;
            try
            {
                // fd 0 is legitimate when stdin is closed, though some SafeFileHandle runtime
                // versions reject it; this desktop process runs with the standard fds open.
                wakeHandle = new SafeFileHandle(CreateEventFd(), ownsHandle: true);
                var deviceHandle = new SafeFileHandle(OpenDevice(), ownsHandle: true);
                _wakeHandle = wakeHandle;
                _deviceHandle = deviceHandle;
                wakeHandle = null;
            }
            finally
            {
                // If opening the device fails, the unpublished wake fd must not escape.
                wakeHandle?.Dispose();
            }
        }
    }

    public unsafe int Read(Span<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty)
        {
            throw new ArgumentException("The evdev read buffer cannot be empty.", nameof(buffer));
        }

        var (deviceHandle, wakeHandle) = GetOpenHandles();
        var pollDescriptors = stackalloc PollDescriptor[2];
        pollDescriptors[0] = new PollDescriptor
        {
            FileDescriptor = deviceHandle.DangerousGetHandle().ToInt32(),
            Events = PollIn,
        };
        pollDescriptors[1] = new PollDescriptor
        {
            FileDescriptor = wakeHandle.DangerousGetHandle().ToInt32(),
            Events = PollIn,
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pollDescriptors[0].ReturnedEvents = 0;
            pollDescriptors[1].ReturnedEvents = 0;

            var pollResult = poll(pollDescriptors, 2, -1);
            if (pollResult < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorInterrupted)
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                throw NativeIoException("poll", error);
            }

            ct.ThrowIfCancellationRequested();
            var wakeEvents = pollDescriptors[1].ReturnedEvents;
            // The eventfd counter is deliberately never drained: level-triggered POLLIN remains
            // pending when Wake lands between cancellation and poll, keeping wakeup lost-signal-free;
            // draining it would reintroduce a hang window.
            if ((wakeEvents & PollIn) != 0)
            {
                throw new OperationCanceledException(ct);
            }

            if ((wakeEvents & (PollError | PollHangUp | PollInvalid)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                throw NativeIoException("poll on evdev wake fd", ErrorBadFileDescriptor);
            }

            var deviceEvents = pollDescriptors[0].ReturnedEvents;
            if ((deviceEvents & PollInvalid) != 0)
            {
                ct.ThrowIfCancellationRequested();
                throw NativeIoException("poll on evdev device fd", ErrorBadFileDescriptor);
            }

            var terminalPollEvent = (deviceEvents & (PollError | PollHangUp)) != 0;
            if ((deviceEvents & PollIn) == 0 && !terminalPollEvent)
            {
                continue;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                nint bytesRead;
                fixed (byte* bufferPointer = buffer)
                {
                    bytesRead = read(deviceHandle, bufferPointer, (nuint)buffer.Length);
                }

                if (bytesRead > 0)
                {
                    if (terminalPollEvent)
                    {
                        throw new IOException(
                            $"poll reported a terminal condition for evdev device {path}."
                        );
                    }

                    return checked((int)bytesRead);
                }

                if (bytesRead == 0)
                {
                    return 0;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorInterrupted)
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }

                if (error == ErrorTryAgain)
                {
                    if (terminalPollEvent)
                    {
                        throw new IOException(
                            $"poll reported a terminal condition for evdev device {path}."
                        );
                    }

                    break;
                }

                ct.ThrowIfCancellationRequested();
                throw NativeIoException("read", error);
            }
        }
    }

    public void Wake()
    {
        SafeFileHandle? wakeHandle;
        lock (_handleLock)
        {
            wakeHandle = _wakeHandle;
        }

        if (wakeHandle is null)
        {
            return;
        }

        var signal = 1UL;
        try
        {
            while (true)
            {
                var bytesWritten = write(wakeHandle, in signal, sizeof(ulong));
                if (bytesWritten == sizeof(ulong))
                {
                    return;
                }

                if (bytesWritten >= 0)
                {
                    throw new IOException(
                        $"eventfd wake for evdev device {path} wrote {bytesWritten} bytes."
                    );
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorInterrupted)
                {
                    continue;
                }

                // A saturated nonblocking eventfd already carries a pending wake signal.
                if (error == ErrorTryAgain)
                {
                    return;
                }

                throw NativeIoException("write to evdev wake fd", error);
            }
        }
        catch (ObjectDisposedException)
        {
            // A disposed wake handle means the read loop has already exited, so the wake's job is done.
        }
    }

    public byte[] QueryPressedKeyBitmap()
    {
        var (deviceHandle, _) = GetOpenHandles();
        var keyBits = new byte[KeyBitmapBytes];
        if (ioctl(deviceHandle, EviocgKey(KeyBitmapBytes), keyBits) < 0)
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
        SafeFileHandle? deviceHandle;
        SafeFileHandle? wakeHandle;
        lock (_handleLock)
        {
            deviceHandle = _deviceHandle;
            wakeHandle = _wakeHandle;
            _deviceHandle = null;
            _wakeHandle = null;
        }

        deviceHandle?.Dispose();
        wakeHandle?.Dispose();
    }

    private int CreateEventFd()
    {
        while (true)
        {
            var fd = eventfd(0, EventFdNonBlock | EventFdCloseOnExec);
            if (fd >= 0)
            {
                return fd;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorInterrupted)
            {
                throw new Win32Exception(error, $"Could not create wake eventfd for {path}.");
            }
        }
    }

    private int OpenDevice()
    {
        while (true)
        {
            var fd = open(
                path,
                OpenReadOnly | OpenNonBlock | OpenCloseOnExec | OpenNoControllingTerminal,
                0
            );
            if (fd >= 0)
            {
                return fd;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorInterrupted)
            {
                throw new Win32Exception(error, $"Could not open evdev device {path}.");
            }
        }
    }

    private (SafeFileHandle Device, SafeFileHandle Wake) GetOpenHandles()
    {
        lock (_handleLock)
        {
            if (_deviceHandle is null || _wakeHandle is null)
            {
                throw new InvalidOperationException($"Evdev device {path} is not open.");
            }

            return (_deviceHandle, _wakeHandle);
        }
    }

    private IOException NativeIoException(string operation, int error)
    {
        return new IOException(
            $"{operation} failed for evdev device {path} (errno {error}).",
            new Win32Exception(error)
        );
    }

    private static nuint EviocgKey(int len)
    {
        return (IocRead << 30)
               | ((uint)len << 16)
               | (EvdevIocType << 8)
               | EviocgKeyNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    // byte[] is blittable, so the source-generated marshaller pins the EV_KEY bitmap buffer.
    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(SafeFileHandle fd, nuint request, [Out] byte[] buf);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int eventfd(uint initialValue, int flags);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags, uint mode);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollDescriptor* fds, nuint count, int timeout);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(SafeFileHandle fd, byte* buffer, nuint count);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(SafeFileHandle fd, in ulong buffer, nuint count);
}
