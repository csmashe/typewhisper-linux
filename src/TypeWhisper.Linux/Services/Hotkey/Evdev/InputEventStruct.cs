using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Linux <c>input_event</c>, 24 bytes on every 64-bit-time target glibc
///     ships today. EV_KEY = 1, EV_SYN = 0. Value: 0 = release, 1 = press,
///     2 = repeat (we ignore repeats — auto-repeat is the OS's job).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long TimeSec;
    public long TimeUsec;
    public ushort Type;
    public ushort Code;
    public int Value;

    public static readonly int SizeBytes = Marshal.SizeOf<InputEvent>();
    // ReSharper disable once UnusedMember.Global  evdev kernel constant (linux/input-event-codes.h EV_SYN); kept for completeness of the input_event vocabulary
    public const ushort EvSyn = 0;
    public const ushort EvKey = 1;
    // ReSharper disable once UnusedMember.Global  evdev input_event value (0 = key release); kept for completeness of the input_event vocabulary
    public const int Released = 0;
    public const int Pressed = 1;
    public const int Repeated = 2;
}