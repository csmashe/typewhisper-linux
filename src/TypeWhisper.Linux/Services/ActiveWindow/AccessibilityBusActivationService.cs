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
    ///     Sets <c>org.a11y.Status.IsEnabled</c> (and <c>ScreenReaderEnabled</c>, which
    ///     Chromium/Electron also key off) on the session bus. Where a GSettings/dconf backend
    ///     is present the a11y launcher mirrors these to
    ///     <c>org.gnome.desktop.interface toolkit-accessibility</c>, so the change can persist
    ///     across sessions rather than resetting at logout. Returns <c>true</c> when the write
    ///     succeeded.
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

    public async Task<bool?> IsActivatedAsync(CancellationToken ct = default)
    {
        var result = await _processRunner
            .RunAsync(
                "busctl",
                ["--user", "get-property", BusName, ObjectPath, StatusInterface, "IsEnabled"],
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

    public async Task<bool> SetActivatedAsync(bool enabled, CancellationToken ct = default)
    {
        // IsEnabled is the flag Chromium/Qt/Firefox gate their a11y tree on; ScreenReaderEnabled
        // is set alongside it because some Chromium builds check that one instead. The result of
        // the primary write is what we report; the secondary is best-effort.
        var ok = await SetPropertyAsync("IsEnabled", enabled, ct).ConfigureAwait(false);

        // When enabling, only mirror to ScreenReaderEnabled if the primary gate actually took:
        // turning the screen-reader flag on by itself does nothing useful and would leave
        // orphaned global state we just reported as failed. When disabling, always clear it so
        // nothing is left behind.
        if (ok || !enabled)
        {
            await SetPropertyAsync("ScreenReaderEnabled", enabled, ct).ConfigureAwait(false);
        }

        return ok;
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
