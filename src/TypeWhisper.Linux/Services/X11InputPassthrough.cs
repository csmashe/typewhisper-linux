using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Empties or restores an X11/XWayland toplevel's input shape, so a mapped-but-hidden overlay
///     lets clicks through even where Mutter clamps its off-screen park back onto a monitor.
///     Best-effort: no-ops without an X display and swallows all failures.
/// </summary>
internal static partial class X11InputPassthrough
{
    private const string X11Lib = "libX11.so.6";
    private const string XextLib = "libXext.so.6";

    // <X11/extensions/shape.h>
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;

    public static void Set(nuint window, bool passThrough)
    {
        if (window == 0)
        {
            return;
        }

        try
        {
            Apply(window, passThrough);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11InputPassthrough] Set({passThrough}) failed: {ex.Message}");
        }
    }

    private static void Apply(nuint window, bool passThrough)
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!XShapeQueryExtension(display, out _, out _))
            {
                return;
            }

            if (passThrough)
            {
                // Zero rectangles: no part of the window accepts pointer input.
                XShapeCombineRectangles(display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
            }
            else
            {
                // A None mask removes the input shape, restoring the default whole-window region.
                XShapeCombineMask(display, window, ShapeInput, 0, 0, 0, ShapeSet);
            }

            _ = XFlush(display);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    [LibraryImport(X11Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? name);

    [LibraryImport(X11Lib)]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(X11Lib)]
    private static partial int XFlush(IntPtr display);

    [LibraryImport(XextLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [LibraryImport(XextLib)]
    private static partial void XShapeCombineRectangles(
        IntPtr display,
        nuint window,
        int destKind,
        int xOffset,
        int yOffset,
        IntPtr rectangles,
        int count,
        int op,
        int ordering);

    [LibraryImport(XextLib)]
    private static partial void XShapeCombineMask(
        IntPtr display,
        nuint window,
        int destKind,
        int xOffset,
        int yOffset,
        nuint source,
        int op);
}
