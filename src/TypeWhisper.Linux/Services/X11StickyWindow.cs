using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Asks the window manager to show an X11/XWayland toplevel on every virtual desktop
///     instead of only the one it was first mapped on. It requests both
///     <c>_NET_WM_STATE_STICKY</c> and <c>_NET_WM_DESKTOP</c> = 0xFFFFFFFF, because some
///     window managers (e.g. Openbox) honor only the latter. The overlay is mapped once
///     and kept alive (driven by Opacity, not Show/Hide — see
///     <see cref="Views.DictationOverlayWindow"/>), so without this it stays pinned to
///     whatever workspace it was mapped on and never follows the user to another workspace. Best-effort: no-ops when there is no X
///     display (pure Wayland without XWayland) and swallows all failures.
/// </summary>
internal static partial class X11StickyWindow
{
    private const string Lib = "libX11.so.6";
    private const int ClientMessage = 33;
    private const int Format32 = 32;

    // _NET_WM_STATE action (EWMH).
    private const long NetWmStateAdd = 1;

    // _NET_WM_DESKTOP value meaning "all desktops" (EWMH).
    private const long AllDesktops = 0xFFFFFFFF;

    // Source indication: 1 = normal application.
    private const long SourceApplication = 1;

    // Root-window event masks for EWMH ClientMessages (EWMH requires both).
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;

    // XEvent is a union; 192 bytes covers its largest member on LP64.
    private const int XEventSize = 192;

    // XClientMessageEvent field offsets on LP64 (see <X11/Xlib.h>).
    private const int OffType = 0; // int
    private const int OffDisplay = 24; // Display*
    private const int OffWindow = 32; // Window (unsigned long)
    private const int OffMessageType = 40; // Atom (unsigned long)
    private const int OffFormat = 48; // int
    private const int OffData = 56; // union { long l[5]; ... } on format 32

    /// <summary>
    ///     Asks the window manager to show the given already-mapped X11 window on all
    ///     desktops. Safe to call repeatedly. Does nothing if the window id is zero or no
    ///     X display is reachable.
    /// </summary>
    public static void MakeSticky(nuint window)
    {
        if (window == 0)
        {
            return;
        }

        try
        {
            Apply(window);
        }
        catch (Exception ex)
        {
            // The overlay still works pinned to one workspace; never fail the show.
            Debug.WriteLine($"[X11StickyWindow] MakeSticky failed: {ex.Message}");
        }
    }

    private static void Apply(nuint window)
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return; // No X server (e.g. pure Wayland without XWayland).
        }

        try
        {
            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);

            var wmState = XInternAtom(display, "_NET_WM_STATE", false);
            var sticky = XInternAtom(display, "_NET_WM_STATE_STICKY", false);
            var wmDesktop = XInternAtom(display, "_NET_WM_DESKTOP", false);

            // _NET_WM_STATE: l[0]=action, l[1]=property atom, l[2]=second property (none),
            // l[3]=source indication.
            SendRootMessage(display, root, window, wmState, NetWmStateAdd, (long)sticky, 0, SourceApplication);

            // _NET_WM_DESKTOP: l[0]=desktop, l[1]=source indication.
            SendRootMessage(display, root, window, wmDesktop, AllDesktops, SourceApplication, 0, 0);

            _ = XFlush(display);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private static void SendRootMessage(
        IntPtr display,
        nuint root,
        nuint window,
        nuint messageType,
        long l0,
        long l1,
        long l2,
        long l3)
    {
        var ev = Marshal.AllocHGlobal(XEventSize);
        try
        {
            ZeroEvent(ev);
            Marshal.WriteInt32(ev, OffType, ClientMessage);
            Marshal.WriteIntPtr(ev, OffDisplay, display);
            Marshal.WriteInt64(ev, OffWindow, (long)window);
            Marshal.WriteInt64(ev, OffMessageType, (long)messageType);
            Marshal.WriteInt32(ev, OffFormat, Format32);
            Marshal.WriteInt64(ev, OffData + 0, l0);
            Marshal.WriteInt64(ev, OffData + 8, l1);
            Marshal.WriteInt64(ev, OffData + 16, l2);
            Marshal.WriteInt64(ev, OffData + 24, l3);
            Marshal.WriteInt64(ev, OffData + 32, 0);

            _ = XSendEvent(
                display,
                root,
                false,
                (nint)(SubstructureNotifyMask | SubstructureRedirectMask),
                ev);
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
        }
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

    // Linux marshals "ANSI" strings as UTF-8, so StringMarshalling.Utf8 matches the prior CharSet.Ansi behavior for ASCII atom names.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(
        IntPtr display,
        string name,
        [MarshalAs(UnmanagedType.Bool)]
        bool onlyIfExists
    );

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
}
