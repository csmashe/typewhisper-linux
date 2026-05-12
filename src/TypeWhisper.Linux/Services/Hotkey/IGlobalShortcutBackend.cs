namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Pluggable global-shortcut delivery mechanism. Phase 1 ships only a
/// SharpHook implementation; Phase 2 adds evdev for Wayland sessions and
/// Phase 3 adds an XDG portal fallback.
/// </summary>
public interface IGlobalShortcutBackend : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsPressRelease { get; }

    /// <summary>
    /// True when the backend delivers shortcuts regardless of which window
    /// owns focus. False for backends that only see events while the
    /// application has the keyboard (SharpHook on Wayland) — the status
    /// panel surfaces this so users aren't told their hotkey is "global"
    /// when in practice it isn't.
    /// </summary>
    bool IsGlobalScope { get; }

    bool IsAvailable();

    Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct);

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
