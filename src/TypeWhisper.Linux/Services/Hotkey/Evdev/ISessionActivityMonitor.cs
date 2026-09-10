namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reports whether raw keyboard input may be consumed for the current login session.
///     Input is allowed only while the session is active and unlocked. Implementations that
///     cannot observe session state preserve the legacy behavior by reporting allowed.
/// </summary>
public interface ISessionActivityMonitor : IAsyncDisposable
{
    bool IsInputAllowed { get; }

    event EventHandler? InputAllowedChanged;

    Task InitializeAsync(CancellationToken ct);
}
