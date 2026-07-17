using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Hotkey.Portal;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Picks the <see cref="IGlobalShortcutBackend" /> for the current session.
///     Wayland order: evdev → XDG portal → SharpHook (focused-only).
///     X11/unknown: SharpHook. Evdev reads <c>/dev/input/event*</c>, so users can
///     opt out via <c>AppSettings.WaylandEvdevHotkeysEnabled</c>, which causes the
///     selector to skip evdev and fall through to portal or SharpHook.
/// </summary>
public sealed class BackendSelector
{
    private readonly Func<IGlobalShortcutBackend> _factory;

    public BackendSelector()
        : this(DefaultFactory(null, null))
    {
    }

    // Takes the settings service, NOT the backend instances: each Resolve() must
    // mint FRESH backends. HotkeyService.SwitchBackendAsync disposes the active
    // backend before re-resolving (on the evdev on/off toggle and after the
    // keyboard-access setup grants access), so reusing injected singletons would
    // hand back a disposed instance on the next switch — e.g. a disposed SharpHook
    // whose RegisterAsync reports success without actually starting the hook,
    // silently breaking the focused-only fallback until restart.
    public BackendSelector(ISettingsService settings)
        : this(DefaultFactory(settings, null))
    {
    }

    public BackendSelector(
        ISettingsService settings,
        ISessionActivityMonitor sessionActivityMonitor
    )
        : this(DefaultFactory(settings, sessionActivityMonitor))
    {
    }

    internal BackendSelector(Func<IGlobalShortcutBackend> factory)
    {
        _factory = factory;
    }

    public IGlobalShortcutBackend Resolve()
    {
        return _factory();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2012:Use ValueTasks correctly", Justification = "Intentional fire-and-forget disposal of the throwaway portal probe instance; XdgPortalGlobalShortcutsBackend.DisposeAsync is a self-contained async ValueTask and awaiting it inside the synchronous factory is unnecessary.")]
    private static Func<IGlobalShortcutBackend> DefaultFactory(
        ISettingsService? settings,
        ISessionActivityMonitor? sessionActivityMonitor
    )
    {
        return () =>
        {
            // X11/unknown: the in-process hook is already global.
            if (!IsWaylandSession())
            {
                return new SharpHookGlobalShortcutBackend();
            }

            // Wayland: evdev first (the full global hotkey set), unless opted out.
            // Probe access via the static check so a rejected evdev never allocates
            // a backend; construct it only when it's the one we return.
            var evdevEnabled = settings?.Current.WaylandEvdevHotkeysEnabled ?? true;
            if (evdevEnabled && InputDeviceAccessCheck.HasKeyboardAccess())
            {
                Trace.WriteLine(
                    "[BackendSelector] evdev backend active — reading keyboard events to detect your configured shortcut. No keystroke content is logged."
                );
                return sessionActivityMonitor is null
                    ? new EvdevGlobalShortcutBackend()
                    : new EvdevGlobalShortcutBackend(sessionActivityMonitor);
            }

            // Portal is a stub (IsAvailable()==false). Probe via a throwaway instance
            // and dispose it when unselected so a future real impl isn't leaked.
            var portal = new XdgPortalGlobalShortcutsBackend();
            if (portal.IsAvailable())
            {
                Trace.WriteLine(
                    "[BackendSelector] Using XDG portal global-shortcuts backend (toggle-only)."
                );
                return portal;
            }

            _ = portal.DisposeAsync();

            Trace.WriteLine(
                evdevEnabled
                    ? "[BackendSelector] Wayland session but evdev unavailable; falling back to SharpHook (focused-only)."
                    : "[BackendSelector] Wayland session but user disabled evdev hotkeys; using focused-only SharpHook."
            );

            // Construct SharpHook ONLY here, when it's the selected backend, so its
            // TaskPoolGlobalHook is never created for a backend we'd discard.
            return new SharpHookGlobalShortcutBackend();
        };
    }

    private static bool IsWaylandSession()
    {
        return WaylandSessionDetector.IsWaylandSession();
    }
}
