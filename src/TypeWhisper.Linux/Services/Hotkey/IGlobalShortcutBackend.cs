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
