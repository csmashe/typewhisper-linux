using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Completes the freedesktop startup-notification sequence so GNOME/Mutter
///     stops showing the "launching" busy cursor. Avalonia (running under
///     XWayland) never maps a window with <c>_NET_STARTUP_ID</c>, so without
///     this the cursor spins until Mutter's ~15–20 s timeout. Single-instance
///     toggle-exits have no window at all — only the broadcast can end it.
///     Best-effort: failures are swallowed; no-ops when there is no
///     <c>DESKTOP_STARTUP_ID</c> or no X display.
/// </summary>
internal static partial class LinuxStartupNotification
{
    private const string Lib = "libX11.so.6";
    private const int ClientMessage = 33;
    private const int Format8 = 8;

    // X event masks the launcher selects for on the root window.
    private const long PropertyChangeMask = 1L << 22;
    private const long StructureNotifyMask = 1L << 17;

    // XEvent is a union; 192 bytes covers its largest member on LP64.
    // Full-size zeroed buffer ensures XSendEvent can't read past our allocation.
    private const int XEventSize = 192;

    // XClientMessageEvent field offsets on LP64 (see <X11/Xlib.h>).
    private const int OffType = 0; // int
    private const int OffDisplay = 24; // Display*
    private const int OffWindow = 32; // Window (unsigned long)
    private const int OffMessageType = 40; // Atom (unsigned long)
    private const int OffFormat = 48; // int
    private const int OffData = 56; // union { char b[20]; ... }
    private const int DataBytes = 20;

    private static int s_done;

    /// <summary>
    ///     Broadcasts startup-notification completion and clears the env vars so
    ///     child processes don't inherit a stale token. Safe to call from any
    ///     thread; runs at most once per process.
    /// </summary>
    public static void NotifyComplete()
    {
        // Idempotent: GUI window-open and the various exit paths may both fire.
        if (Interlocked.Exchange(ref s_done, 1) != 0)
        {
            return;
        }

        var startupId = Environment.GetEnvironmentVariable("DESKTOP_STARTUP_ID");

        // Clear regardless so children never inherit a consumed token.
        Environment.SetEnvironmentVariable("DESKTOP_STARTUP_ID", null);
        Environment.SetEnvironmentVariable("XDG_ACTIVATION_TOKEN", null);

        if (string.IsNullOrEmpty(startupId))
        {
            return;
        }

        try
        {
            Broadcast(startupId);
        }
        catch (Exception ex)
        {
            // Never let launch feedback cleanup affect startup.
            Debug.WriteLine($"[StartupNotification] Completion broadcast failed: {ex.Message}");
        }
    }

    private static void Broadcast(string startupId)
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return; // No X server (e.g. headless / pure-Wayland without XWayland).
        }

        var ev = IntPtr.Zero;
        nuint window = 0;
        try
        {
            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);

            // A throwaway never-mapped window owns the broadcast, per the spec.
            window = XCreateSimpleWindow(display, root, -100, -100, 1, 1, 0, 0, 0);
            _ = XSelectInput(display, window, (nint)(PropertyChangeMask | StructureNotifyMask));

            var begin = XInternAtom(display, "_NET_STARTUP_INFO_BEGIN", false);
            var info = XInternAtom(display, "_NET_STARTUP_INFO", false);

            var payload = BuildRemoveMessage(startupId);

            ev = Marshal.AllocHGlobal(XEventSize);
            var offset = 0;
            var first = true;
            // Messages are 20-byte (format-8) ClientMessage chunks; first carries
            // _NET_STARTUP_INFO_BEGIN, the rest _NET_STARTUP_INFO.
            while (offset < payload.Length)
            {
                ZeroEvent(ev);
                Marshal.WriteInt32(ev, OffType, ClientMessage);
                Marshal.WriteIntPtr(ev, OffDisplay, display);
                Marshal.WriteInt64(ev, OffWindow, (long)window);
                Marshal.WriteInt64(ev, OffMessageType, (long)(first ? begin : info));
                Marshal.WriteInt32(ev, OffFormat, Format8);

                var n = Math.Min(DataBytes, payload.Length - offset);
                for (var i = 0; i < n; i++)
                {
                    Marshal.WriteByte(ev, OffData + i, payload[offset + i]);
                }

                _ = XSendEvent(display, root, false, (nint)PropertyChangeMask, ev);
                offset += DataBytes;
                first = false;
            }

            _ = XFlush(display);
        }
        finally
        {
            if (ev != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ev);
            }

            if (window != 0)
            {
                _ = XDestroyWindow(display, window);
            }

            _ = XCloseDisplay(display);
        }
    }

    /// <summary>"remove: ID=&lt;id&gt;\0" with spaces, quotes, and backslashes escaped per the spec.</summary>
    private static byte[] BuildRemoveMessage(string startupId)
    {
        var sb = new StringBuilder("remove: ID=");
        foreach (var c in startupId)
        {
            if (c is ' ' or '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        var text = Encoding.UTF8.GetBytes(sb.ToString());
        var withNul = new byte[text.Length + 1];
        Array.Copy(text, withNul, text.Length);
        // last byte already 0 — the terminating NUL the launcher expects.
        return withNul;
    }

    private static void ZeroEvent(IntPtr ev)
    {
        for (var i = 0; i < XEventSize; i += 8)
        {
            Marshal.WriteInt64(ev, i, 0);
        }
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? name);

    [LibraryImport(Lib)]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(Lib)]
    private static partial int XDefaultScreen(IntPtr display);

    [LibraryImport(Lib)]
    private static partial nuint XRootWindow(IntPtr display, int screen);

    [LibraryImport(Lib)]
    private static partial nuint XCreateSimpleWindow(
        IntPtr display,
        nuint parent,
        int x,
        int y,
        uint width,
        uint height,
        uint borderWidth,
        nuint border,
        nuint background
    );

    // Linux marshals "ANSI" strings as UTF-8, so StringMarshalling.Utf8 matches the prior CharSet.Ansi behavior for ASCII atom names.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(
        IntPtr display,
        string name,
        [MarshalAs(UnmanagedType.Bool)]
        bool onlyIfExists
    );

    [LibraryImport(Lib)]
    private static partial int XSelectInput(IntPtr display, nuint window, nint eventMask);

    [LibraryImport(Lib)]
    private static partial int XSendEvent(
        IntPtr display,
        nuint window,
        [MarshalAs(UnmanagedType.Bool)]
        bool propagate,
        nint eventMask,
        IntPtr eventSend
    );

    [LibraryImport(Lib)]
    private static partial int XFlush(IntPtr display);

    [LibraryImport(Lib)]
    private static partial int XDestroyWindow(IntPtr display, nuint window);
}