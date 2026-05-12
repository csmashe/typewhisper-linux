namespace TypeWhisper.Linux.Services.Hotkey.Portal;

/// <summary>
/// Toggle-only fallback for Wayland sessions where evdev isn't available
/// (user not in <c>input</c> group, sandboxed app, etc.). Talks to
/// <c>org.freedesktop.portal.GlobalShortcuts</c> via D-Bus.
///
/// IMPLEMENTATION STATUS: stub. <see cref="IsAvailable"/> returns false
/// so <see cref="BackendSelector"/> currently skips this backend and
/// falls through to focused-only SharpHook. The wiring is in place so
/// the real D-Bus implementation can be slotted in without further
/// architecture changes.
///
/// TODO — real impl outline:
///   1. Add a D-Bus client dependency (e.g. Tmds.DBus.Protocol).
///   2. Probe <c>org.freedesktop.portal.Desktop</c> exists on the session
///      bus + exposes the <c>GlobalShortcuts</c> interface.
///   3. Call <c>CreateSession</c> → wait for request response → get
///      session handle; persist it across restarts so the user only
///      sees the desktop's binding dialog on first run.
///   4. Call <c>BindShortcuts(session, [(id, options)], parent_window,
///      options)</c> with stable IDs:
///        typewhisper.dictation.toggle
///        typewhisper.prompt-palette
///        typewhisper.recent
///        typewhisper.copy-last
///        typewhisper.transform-selection
///   5. Subscribe to the <c>Activated(session, id, timestamp, options)</c>
///      signal. The portal's <c>Deactivated</c> signal is unreliable
///      across DEs, so treat every binding as press-only and report
///      <see cref="GlobalShortcutRegistrationResult.RequiresToggleMode"/>
///      = true so the UI can warn users on PushToTalk/Hybrid modes.
/// </summary>
public sealed class XdgPortalGlobalShortcutsBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-xdg-portal";

    public string Id => BackendId;
    public string DisplayName => "XDG Desktop Portal";
    public bool SupportsPressRelease => false;
    public bool IsGlobalScope => true;

    public bool IsAvailable() => false;

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
        => Task.FromResult(new GlobalShortcutRegistrationResult(
            Success: false,
            BackendId: BackendId,
            UserMessage: "XDG portal global-shortcuts backend is not yet implemented.",
            RequiresToggleMode: true,
            TroubleshootingCommand: null));

    public Task UnregisterAsync(CancellationToken ct) => Task.CompletedTask;

    public event EventHandler? DictationToggleRequested { add { } remove { } }
    public event EventHandler? DictationStartRequested { add { } remove { } }
    public event EventHandler? DictationStopRequested { add { } remove { } }
    public event EventHandler? PromptPaletteRequested { add { } remove { } }
    public event EventHandler? TransformSelectionRequested { add { } remove { } }
    public event EventHandler? RecentTranscriptionsRequested { add { } remove { } }
    public event EventHandler? CopyLastTranscriptionRequested { add { } remove { } }
    public event EventHandler? CancelRequested { add { } remove { } }
    public event EventHandler<string>? Failed { add { } remove { } }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
