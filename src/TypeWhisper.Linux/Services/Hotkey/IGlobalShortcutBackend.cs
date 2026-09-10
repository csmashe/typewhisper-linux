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
    ///     True when shortcuts fire regardless of focus. False for backends that
    ///     only see events while the app has the keyboard (e.g. SharpHook on
    ///     Wayland) — surfaced in the status panel to avoid misleading users.
    /// </summary>
    bool IsGlobalScope { get; }

    // ReSharper disable once UnusedMemberInSuper.Global -- interface contract member; callers invoke IsAvailable on concrete backends (e.g. BackendSelector probes portal.IsAvailable())
    bool IsAvailable();

    Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        // ReSharper disable once UnusedParameter.Global -- CancellationToken mandated by the async interface contract; some backends complete synchronously and don't observe it
        CancellationToken ct
    );

    // ReSharper disable once UnusedMember.Global  interface contract member, part of the IGlobalShortcutBackend surface (implemented by every backend)
    // ReSharper disable once UnusedParameter.Global -- CancellationToken mandated by the async interface contract; backends complete synchronously and don't observe it
    Task UnregisterAsync(CancellationToken ct);

    event EventHandler? DictationToggleRequested;
    event EventHandler? DictationStartRequested;
    event EventHandler? DictationStopRequested;

    // Session-loss teardown (e.g. screen lock): discard any active recording without transcription
    // or text insertion. Only the evdev backend, which gates on session activity, raises this.
    event EventHandler? DictationDiscardRequested;
    event EventHandler? PromptPaletteRequested;
    event EventHandler? TransformSelectionRequested;
    event EventHandler? RecentTranscriptionsRequested;
    event EventHandler? CopyLastTranscriptionRequested;
    event EventHandler? CancelRequested;
    event EventHandler<string>? PromptActionRequested;

    // Profile hotkeys: start/toggle carry the profile id; stop is parameterless
    // (id consumed at session start). Portal stub leaves these unimplemented.
    event EventHandler<string>? ProfileDictationToggleRequested;
    event EventHandler<string>? ProfileDictationStartRequested;
    event EventHandler? ProfileDictationStopRequested;
    event EventHandler<string>? ProfileTextProcessingRequested;

    event EventHandler<string>? Failed;
}