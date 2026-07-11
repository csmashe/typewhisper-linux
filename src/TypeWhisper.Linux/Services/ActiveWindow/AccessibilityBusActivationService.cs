using System.Diagnostics;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Reads and toggles the session-bus accessibility activation flag
///     (<c>org.a11y.Status.IsEnabled</c>). GTK apps expose their accessibility tree
///     unconditionally, but Chromium/Electron (VS Code) and Qt apps only build theirs when
///     this flag is <c>true</c> at app launch — and most desktops leave it <c>false</c> by
///     default (GNOME through 50 mirrors the gsettings <c>toolkit-accessibility</c> key,
///     whose default is false; bare wlroots sessions like Hyprland set nothing at all), so
///     those apps expose no readable text and target-app correction learning silently
///     no-ops. Behind an interface so the settings ViewModel can be unit-tested with a fake.
/// </summary>
public interface IAccessibilityBusActivation
{
    /// <summary>
    ///     Reads <c>org.a11y.Status.IsEnabled</c> from the session bus. Returns <c>null</c>
    ///     when the value can't be determined (busctl missing, bus unreachable, unparsable).
    /// </summary>
    Task<bool?> IsActivatedAsync(CancellationToken ct = default);

    /// <summary>
    ///     Reads <c>org.a11y.Status.ScreenReaderEnabled</c> (read-only — see
    ///     <see cref="SetActivatedAsync" /> for why it is never written). <c>true</c> means a
    ///     screen reader (Orca) is or was active this session and may rely on the
    ///     accessibility flag, so removal must be refused. <c>null</c> = indeterminate;
    ///     callers should fail closed.
    /// </summary>
    Task<bool?> IsScreenReaderActiveAsync(CancellationToken ct = default);

    /// <summary>
    ///     Sets <c>org.a11y.Status.IsEnabled</c> on the session bus. Where a GSettings/dconf
    ///     backend is present the a11y launcher mirrors it to
    ///     <c>org.gnome.desktop.interface toolkit-accessibility</c>, so the change can persist
    ///     across sessions rather than resetting at logout. Returns <c>true</c> when the write
    ///     succeeded.
    ///     <para>
    ///         Deliberately never touches <c>ScreenReaderEnabled</c>: GNOME mirrors that one to
    ///         <c>org.gnome.desktop.a11y.applications screen-reader-enabled</c>, which LAUNCHES
    ///         the Orca screen reader and makes the whole desktop speak. Chromium reads only
    ///         <c>IsEnabled</c>, and Qt accepts either, so <c>IsEnabled</c> alone is sufficient.
    ///     </para>
    /// </summary>
    Task<bool> SetActivatedAsync(bool enabled, CancellationToken ct = default);
}

public sealed class AccessibilityBusActivationService : IAccessibilityBusActivation
{
    private const string BusName = "org.a11y.Bus";
    private const string ObjectPath = "/org/a11y/bus";
    private const string StatusInterface = "org.a11y.Status";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _processRunner;

    public AccessibilityBusActivationService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public Task<bool?> IsActivatedAsync(CancellationToken ct = default)
    {
        return ReadBoolPropertyAsync("IsEnabled", ct);
    }

    public Task<bool?> IsScreenReaderActiveAsync(CancellationToken ct = default)
    {
        return ReadBoolPropertyAsync("ScreenReaderEnabled", ct);
    }

    private async Task<bool?> ReadBoolPropertyAsync(string property, CancellationToken ct)
    {
        var result = await _processRunner
            .RunAsync(
                "busctl",
                ["--user", "get-property", BusName, ObjectPath, StatusInterface, property],
                timeout: s_timeout,
                ct: ct
            )
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        // busctl prints the boolean variant as "b true" / "b false".
        var text = result.StandardOutput.Trim();
        if (text.EndsWith("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.EndsWith("false", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    public Task<bool> SetActivatedAsync(bool enabled, CancellationToken ct = default)
    {
        // IsEnabled is the flag Chromium/Qt/Firefox gate their a11y tree on, and the ONLY
        // property we write. Never set ScreenReaderEnabled here — GNOME mirrors it into the
        // screen-reader-enabled gsettings key, which launches Orca and makes the desktop speak.
        return SetPropertyAsync("IsEnabled", enabled, ct);
    }

    private async Task<bool> SetPropertyAsync(string property, bool value, CancellationToken ct)
    {
        var result = await _processRunner
            .RunAsync(
                "busctl",
                [
                    "--user",
                    "set-property",
                    BusName,
                    ObjectPath,
                    StatusInterface,
                    property,
                    "b",
                    value ? "true" : "false"
                ],
                timeout: s_timeout,
                ct: ct
            )
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            Trace.WriteLine(
                $"[A11yBusActivation] Failed to set {property}={value}: {result.StandardError.Trim()}"
            );
        }

        return result.Succeeded;
    }
}
