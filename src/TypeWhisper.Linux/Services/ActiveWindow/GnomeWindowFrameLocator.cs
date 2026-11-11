using System.Globalization;
using System.Text.Json;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     A window's frame in the compositor's logical coordinates, plus the bounds of the whole
///     logical desktop. Xwayland can scale those coordinates (fractional scaling), so consumers
///     map the frame onto their own screen space through the two desktops' bounds. A null desktop
///     marks a frame already in screen coordinates (X11 root), which needs no mapping.
/// </summary>
public readonly record struct CompositorWindowFrame(AtSpiScreenRect Frame, AtSpiScreenRect? Desktop);

/// <summary>
///     Locates a top-level window's on-screen frame, for anchoring feedback when AT-SPI can't
///     report global element extents (native Wayland toolkits only know window-relative ones).
/// </summary>
public interface IWindowFrameLocator
{
    /// <summary>Returns an opaque handle for the currently focused window, or null.</summary>
    Task<string?> TryGetFocusedWindowHandleAsync(CancellationToken ct);

    /// <summary>Returns the window's current frame and the logical desktop bounds, or null.</summary>
    Task<CompositorWindowFrame?> TryGetWindowFrameAsync(string handle, CancellationToken ct);
}

/// <summary>
///     GNOME Wayland frame locator backed by the Window Calls extension's <c>List</c> and
///     <c>GetFrameRect</c> methods. Inert (returns null) outside GNOME Wayland, where AT-SPI
///     extents are already global, and when the extension isn't installed.
/// </summary>
public sealed class GnomeWindowFrameLocator : IWindowFrameLocator
{
    private const string DBusDest = "org.gnome.Shell";
    private const string ObjectPath = "/org/gnome/Shell/Extensions/Windows";
    private const string Interface = "org.gnome.Shell.Extensions.Windows";

    // Bounds each gdbus call so a stalled shell can't hold up correction learning.
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(1);

    private readonly bool _applicable;
    private readonly Func<CancellationToken, Task<AtSpiScreenRect?>> _logicalDesktop;
    private readonly ProviderProcessRunner _processRunner;

    public GnomeWindowFrameLocator()
        : this(new ProcessRunner(), IsGnomeWayland(), MutterDisplayConfig.TryGetLogicalDesktopAsync) { }

    internal GnomeWindowFrameLocator(
        IProcessRunner processRunner,
        bool applicable,
        Func<CancellationToken, Task<AtSpiScreenRect?>> logicalDesktop
    )
    {
        _processRunner = new ProviderProcessRunner(processRunner);
        _applicable = applicable;
        _logicalDesktop = logicalDesktop;
    }

    public async Task<string?> TryGetFocusedWindowHandleAsync(CancellationToken ct)
    {
        if (!_applicable)
        {
            return null;
        }

        var output = await CallAsync("List", [], ct).ConfigureAwait(false);
        return output is null ? null : GnomeWindowCallsProvider.ParseFocusedWindow(output)?.WindowId;
    }

    public async Task<CompositorWindowFrame?> TryGetWindowFrameAsync(string handle, CancellationToken ct)
    {
        if (!_applicable || !uint.TryParse(handle, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var output = await CallAsync("GetFrameRect", [$"uint32 {id}"], ct).ConfigureAwait(false);
        if (output is null || ParseFrameRect(output) is not { } frame)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_callTimeout);
        var desktop = await _logicalDesktop(timeout.Token).ConfigureAwait(false);
        return desktop is { } bounds ? new CompositorWindowFrame(frame, bounds) : null;
    }

    internal static AtSpiScreenRect? ParseFrameRect(string gvariantOutput)
    {
        var json = GnomeWindowCallsProvider.UnwrapGvariantString(gvariantOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (
                root.ValueKind == JsonValueKind.Object
                && TryGetInt(root, "x") is { } x
                && TryGetInt(root, "y") is { } y
                && TryGetInt(root, "width") is { } width and > 0
                && TryGetInt(root, "height") is { } height and > 0
            )
            {
                return new AtSpiScreenRect(x, y, width, height);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private async Task<string?> CallAsync(string method, IReadOnlyList<string> args, CancellationToken ct)
    {
        string[] command =
        [
            "call",
            "--session",
            "--dest",
            DBusDest,
            "--object-path",
            ObjectPath,
            "--method",
            $"{Interface}.{method}",
            .. args,
        ];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_callTimeout);
        var (exit, output) = await _processRunner
            .RunAsync("gdbus", command, timeout.Token)
            .ConfigureAwait(false);
        return exit == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static bool IsGnomeWayland()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.ToLowerInvariant();
        return desktop is not null
            && (desktop.Contains("gnome") || desktop.Contains("ubuntu"))
            && WaylandSessionDetector.IsWaylandSession()
            && DesktopDetector.BinaryExists("gdbus");
    }
}
