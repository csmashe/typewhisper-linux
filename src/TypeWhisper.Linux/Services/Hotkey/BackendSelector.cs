namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Picks which <see cref="IGlobalShortcutBackend"/> to use for the current
/// session. Phase 1 always returns the SharpHook backend; Phase 2 will pick
/// between SharpHook (X11) and evdev (Wayland); Phase 3 adds an XDG portal
/// fallback.
/// </summary>
public sealed class BackendSelector
{
    private readonly Func<IGlobalShortcutBackend> _factory;

    public BackendSelector()
        : this(() => new SharpHookGlobalShortcutBackend()) { }

    public BackendSelector(SharpHookGlobalShortcutBackend sharpHook)
        : this(() => sharpHook) { }

    internal BackendSelector(Func<IGlobalShortcutBackend> factory)
    {
        _factory = factory;
    }

    public IGlobalShortcutBackend Resolve() => _factory();
}
