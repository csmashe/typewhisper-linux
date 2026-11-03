using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Hyprland active-window provider. Gated on the
///     <c>HYPRLAND_INSTANCE_SIGNATURE</c> environment variable, which Hyprland
///     only sets inside a live session. Queries <c>hyprctl activewindow -j</c>
///     and parses the JSON payload — <c>class</c> maps to ProcessName / AppId,
///     <c>title</c> to Title, and <c>address</c> to WindowId.
/// </summary>
public sealed class HyprlandActiveWindowProvider : IActiveWindowProvider
{
    private readonly ProviderProcessRunner _processRunner;

    public HyprlandActiveWindowProvider()
        : this(new ProcessRunner()) { }

    public HyprlandActiveWindowProvider(IProcessRunner processRunner)
    {
        _processRunner = new ProviderProcessRunner(processRunner);
    }

    public string Name => "hyprland";

    public bool IsApplicable()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))
            && DesktopDetector.BinaryExists("hyprctl");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await _processRunner
                .RunAsync("hyprctl", ["activewindow", "-j"], ct)
                .ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var klass = TryGetString(root, "class");
            var title = TryGetString(root, "title");
            var address = TryGetString(root, "address");
            var pidValue =
                root.TryGetProperty("pid", out var pidProp)
                && pidProp.ValueKind == JsonValueKind.Number
                    ? pidProp.GetInt32()
                    : 0;

            // Skip our own windows (overlay can momentarily hold focus) so the
            // transcript is aimed at the user's target app, not TypeWhisper itself.
            if (pidValue > 0 && pidValue == Environment.ProcessId)
            {
                return null;
            }

            // /proc/PID/comm gives the binary identity that user profiles match against
            // (consistent with the X11/xdotool path); see GnomeWindowCallsProvider for rationale.
            var rawIdentity = pidValue > 0 ? TryReadProcComm(pidValue) : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
            {
                rawIdentity = klass;
            }

            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                string.IsNullOrWhiteSpace(processName) ? null : processName,
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(address) ? null : address,
                string.IsNullOrWhiteSpace(klass) ? null : klass,
                Name
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static string? TryReadProcComm(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/comm";
            return !File.Exists(path) ? null : File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }
}
