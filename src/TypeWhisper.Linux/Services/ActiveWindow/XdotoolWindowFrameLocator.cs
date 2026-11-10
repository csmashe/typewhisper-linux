using System.Globalization;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     X11 frame locator backed by <c>xdotool</c>. Reports the window's root-window (device pixel)
///     geometry, which lets the toast reject AT-SPI extents in another coordinate space, such as a
///     GTK app running with <c>GDK_SCALE=2</c> whose extents are half its real position. Inert in
///     Wayland sessions and when xdotool isn't installed.
/// </summary>
public sealed partial class XdotoolWindowFrameLocator : IWindowFrameLocator
{
    // Bounds each xdotool call so a stalled X server can't hold up correction learning.
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(1);

    private readonly bool _applicable;
    private readonly ProviderProcessRunner _processRunner;
    private readonly Func<AtSpiScreenRect?> _rootWindow;

    public XdotoolWindowFrameLocator()
        : this(
            new ProcessRunner(),
            !WaylandSessionDetector.IsWaylandSession() && DesktopDetector.BinaryExists("xdotool"),
            TryGetRootWindow
        ) { }

    internal XdotoolWindowFrameLocator(IProcessRunner processRunner, bool applicable, Func<AtSpiScreenRect?> rootWindow)
    {
        _processRunner = new ProviderProcessRunner(processRunner);
        _applicable = applicable;
        _rootWindow = rootWindow;
    }

    public async Task<string?> TryGetFocusedWindowHandleAsync(CancellationToken ct)
    {
        if (!_applicable)
        {
            return null;
        }

        var output = await RunAsync(["getactivewindow"], ct).ConfigureAwait(false);
        return ulong.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    public async Task<CompositorWindowFrame?> TryGetWindowFrameAsync(string handle, CancellationToken ct)
    {
        if (!_applicable || !ulong.TryParse(handle, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var geometry = await RunAsync(["getwindowgeometry", "--shell", handle], ct).ConfigureAwait(false);
        if (geometry is null || ParseWindowGeometry(geometry) is not { } frame)
        {
            return null;
        }

        return _rootWindow() is { } desktop ? new CompositorWindowFrame(frame, desktop) : null;
    }

    // `getwindowgeometry --shell` prints X=, Y=, WIDTH=, HEIGHT= lines in root coordinates.
    internal static AtSpiScreenRect? ParseWindowGeometry(string output)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (
                separator > 0
                && int.TryParse(line.AsSpan(separator + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            )
            {
                values[line[..separator]] = value;
            }
        }

        return values.TryGetValue("X", out var x)
            && values.TryGetValue("Y", out var y)
            && values.TryGetValue("WIDTH", out var width) && width > 0
            && values.TryGetValue("HEIGHT", out var height) && height > 0
            ? new AtSpiScreenRect(x, y, width, height)
            : null;
    }

    // The root window spans every monitor, so window geometry needs no rescaling against it.
    // `xdotool getdisplaygeometry` reports only the first monitor under RandR, so ask Xlib.
    private static AtSpiScreenRect? TryGetRootWindow()
    {
        try
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var screen = XDefaultScreen(display);
                var width = XDisplayWidth(display, screen);
                var height = XDisplayHeight(display, screen);
                return width > 0 && height > 0 ? new AtSpiScreenRect(0, 0, width, height) : null;
            }
            finally
            {
                _ = XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_callTimeout);
        var (exit, output) = await _processRunner.RunAsync("xdotool", args, timeout.Token).ConfigureAwait(false);
        return exit == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport("libX11.so.6")]
    private static partial int XDefaultScreen(IntPtr display);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayWidth(IntPtr display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayHeight(IntPtr display, int screen);
}
