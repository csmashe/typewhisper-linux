namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// Result of asking a backend to register a <see cref="GlobalShortcutSet"/>.
/// Backends that can deliver press/release events (SharpHook, evdev) set
/// <paramref name="RequiresToggleMode"/> to false; portal/CLI-only backends
/// set it true and the coordinator forces Toggle mode while that backend is
/// active.
/// </summary>
public sealed record GlobalShortcutRegistrationResult(
    bool Success,
    string BackendId,
    string? UserMessage,
    bool RequiresToggleMode,
    string? TroubleshootingCommand);
