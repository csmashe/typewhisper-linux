namespace TypeWhisper.Linux.Services.Hotkey.Portal;

/// <summary>
///     Toggle-only fallback for Wayland sessions where evdev is unavailable
///     (user not in <c>input</c> group, sandboxed app, etc.). Would talk to
///     <c>org.freedesktop.portal.GlobalShortcuts</c> via D-Bus.
///     IMPLEMENTATION STATUS: stub — <see cref="IsAvailable" /> returns false
///     so <see cref="BackendSelector" /> skips it and falls through to focused-only SharpHook.
///     The wiring is in place for a future real implementation. Only worth building if
///     a sandboxed/Flatpak distribution makes the portal the sole Wayland option.
///     Implementation outline:
///     1. Add a D-Bus client (e.g. Tmds.DBus.Protocol) and probe <c>org.freedesktop.portal.Desktop</c>.
///     2. Call <c>CreateSession</c>, persist the handle across restarts (so binding dialog shows once).
///     3. Call <c>BindShortcuts</c> with stable IDs: <c>typewhisper.dictation.toggle</c>,
///        <c>typewhisper.prompt-palette</c>, <c>typewhisper.recent</c>, <c>typewhisper.copy-last</c>,
///        <c>typewhisper.transform-selection</c>.
///     4. Subscribe to <c>Activated</c> signal. Treat as press-only (portal's <c>Deactivated</c>
///        is unreliable); set <see cref="GlobalShortcutRegistrationResult.RequiresToggleMode" />=true.
/// </summary>
public sealed class XdgPortalGlobalShortcutsBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-xdg-portal";

    public string Id => BackendId;
    public string DisplayName => "XDG Desktop Portal";
    public bool SupportsPressRelease => false;
    public bool IsGlobalScope => true;

    public bool IsAvailable()
    {
        return false;
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        return Task.FromResult(
            new GlobalShortcutRegistrationResult(
                false,
                BackendId,
                "XDG portal global-shortcuts backend is not yet implemented.",
                true,
                null
            )
        );
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public event EventHandler? DictationToggleRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? DictationStartRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? DictationStopRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PromptPaletteRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? TransformSelectionRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? RecentTranscriptionsRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? CopyLastTranscriptionRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? CancelRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? PromptActionRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? ProfileDictationToggleRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? ProfileDictationStartRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? ProfileDictationStopRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? ProfileTextProcessingRequested
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Failed
    {
        add { }
        remove { }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}