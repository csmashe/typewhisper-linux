using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Hotkey.Portal;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Picks which <see cref="IGlobalShortcutBackend"/> to use for the current
/// session.
///
/// Selection order on Wayland: evdev → SharpHook (focused-only).
/// Selection order on X11 or unknown sessions: SharpHook.
///
/// The evdev backend reads <c>/dev/input/event*</c>, so we respect a user
/// opt-out via <c>AppSettings.WaylandEvdevHotkeysEnabled</c> — when set
/// false the selector skips evdev even if it would otherwise pick it.
/// Phase 3 will splice an XDG-portal backend between evdev and SharpHook
/// on Wayland sessions where the user isn't in the <c>input</c> group.
/// </summary>
public sealed class BackendSelector
{
    private readonly Func<IGlobalShortcutBackend> _factory;

    public BackendSelector()
        : this(DefaultFactory(settings: null)) { }

    public BackendSelector(
        SharpHookGlobalShortcutBackend sharpHook,
        EvdevGlobalShortcutBackend evdev,
        XdgPortalGlobalShortcutsBackend portal,
        ISettingsService settings)
        : this(() => Resolve(evdev, portal, sharpHook, evdevEnabled: settings.Current.WaylandEvdevHotkeysEnabled)) { }

    internal BackendSelector(Func<IGlobalShortcutBackend> factory)
    {
        _factory = factory;
    }

    public IGlobalShortcutBackend Resolve() => _factory();

    private static Func<IGlobalShortcutBackend> DefaultFactory(ISettingsService? settings) => () =>
    {
        var sharpHook = new SharpHookGlobalShortcutBackend();
        if (!IsWaylandSession()) return sharpHook;
        var evdev = new EvdevGlobalShortcutBackend();
        var portal = new XdgPortalGlobalShortcutsBackend();
        var enabled = settings?.Current.WaylandEvdevHotkeysEnabled ?? true;
        return Resolve(evdev, portal, sharpHook, enabled);
    };

    private static IGlobalShortcutBackend Resolve(
        EvdevGlobalShortcutBackend evdev,
        XdgPortalGlobalShortcutsBackend portal,
        SharpHookGlobalShortcutBackend sharpHook,
        bool evdevEnabled)
    {
        if (!IsWaylandSession()) return sharpHook;

        if (evdevEnabled && evdev.IsAvailable())
        {
            Trace.WriteLine("[BackendSelector] evdev backend active — reading keyboard events to detect your configured shortcut. No keystroke content is logged.");
            return evdev;
        }

        if (portal.IsAvailable())
        {
            Trace.WriteLine("[BackendSelector] Using XDG portal global-shortcuts backend (toggle-only).");
            return portal;
        }

        if (!evdevEnabled)
            Trace.WriteLine("[BackendSelector] Wayland session but user disabled evdev hotkeys; using focused-only SharpHook.");
        else
            Trace.WriteLine("[BackendSelector] Wayland session but evdev unavailable; falling back to SharpHook (focused-only).");
        return sharpHook;
    }

    private static bool IsWaylandSession()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase);
    }
}
