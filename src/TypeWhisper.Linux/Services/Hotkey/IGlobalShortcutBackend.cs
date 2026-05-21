namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
///     Pluggable global-shortcut delivery mechanism. Concrete backends are
///     SharpHook (X11 and Wayland focused-only), evdev (Wayland, raw device
///     access), and XDG portal (Wayland, session-bus protocol).
/// </summary>
public interface IGlobalShortcutBackend : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsPressRelease { get; }

    /// <summary>
    ///     True when the backend delivers shortcuts regardless of which window
    ///     owns focus. False for backends that only see events while the
    ///     application has the keyboard (SharpHook on Wayland) — the status
    ///     panel surfaces this so users aren't told their hotkey is "global"
    ///     when in practice it isn't.
    /// </summary>
    bool IsGlobalScope { get; }

    bool IsAvailable();

    Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    );

    Task UnregisterAsync(CancellationToken ct);

    event EventHandler? DictationToggleRequested;
    event EventHandler? DictationStartRequested;
    event EventHandler? DictationStopRequested;
    event EventHandler? PromptPaletteRequested;
    event EventHandler? TransformSelectionRequested;
    event EventHandler? RecentTranscriptionsRequested;
    event EventHandler? CopyLastTranscriptionRequested;
    event EventHandler? CancelRequested;
    event EventHandler<string>? Failed;
}