using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Puts an X11/XWayland toplevel in the ICCCM "No Input" focus model (<c>WM_HINTS</c> input =
///     False), so the window manager never makes it the keyboard-active window, not even when it is
///     clicked. ShowActivated=false only covers the initial map. Without this, KWin activates a
///     window on click and keeps it active after it is hidden. Pointer clicks still reach the window.
///     Best-effort: no-ops without an X display and swallows all failures.
/// </summary>
internal static partial class X11NoInputFocus
{
    private const string Lib = "libX11.so.6";

    // <X11/Xutil.h> WM_HINTS flag for the input field.
    private const long InputHint = 1L << 0;

    public static void Apply(nuint window)
    {
        if (window == 0)
        {
            return;
        }

        try
        {
            SetNoInput(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11NoInputFocus] Apply failed: {ex.Message}");
        }
    }

    private static void SetNoInput(nuint window)
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Read-modify-write so any hints set elsewhere (icon, urgency, group) survive.
            var hints = new XWmHints();
            var existing = XGetWMHints(display, window);
            if (existing != IntPtr.Zero)
            {
                hints = Marshal.PtrToStructure<XWmHints>(existing);
                _ = XFree(existing);
            }

            hints.Flags |= (nint)InputHint;
            hints.Input = 0;
            _ = XSetWMHints(display, window, ref hints);
            _ = XFlush(display);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    // XWMHints from <X11/Xutil.h>; long/XID fields are pointer-sized on LP64.
    [StructLayout(LayoutKind.Sequential)]
    private struct XWmHints
    {
        public nint Flags;
        public int Input;
        public int InitialState;
        public nuint IconPixmap;
        public nuint IconWindow;
        public int IconX;
        public int IconY;
        public nuint IconMask;
        public nuint WindowGroup;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? name);

    [LibraryImport(Lib)]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(Lib)]
    private static partial int XFlush(IntPtr display);

    [LibraryImport(Lib)]
    private static partial int XFree(IntPtr data);

    [LibraryImport(Lib)]
    private static partial IntPtr XGetWMHints(IntPtr display, nuint window);

    [LibraryImport(Lib)]
    private static partial int XSetWMHints(IntPtr display, nuint window, ref XWmHints hints);
}
