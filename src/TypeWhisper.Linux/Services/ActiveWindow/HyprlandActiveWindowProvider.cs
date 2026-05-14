using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// Hyprland active-window provider. Gated on the
/// <c>HYPRLAND_INSTANCE_SIGNATURE</c> environment variable, which Hyprland
/// only sets inside a live session. Queries <c>hyprctl activewindow -j</c>
/// and parses the JSON payload — <c>class</c> maps to ProcessName / AppId,
/// <c>title</c> to Title, and <c>address</c> to WindowId.
/// </summary>
public sealed class HyprlandActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "hyprland";

    public bool IsApplicable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
            return false;
        return DesktopDetector.BinaryExists("hyprctl");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await ProviderProcessRunner
                .RunAsync("hyprctl", "activewindow -j", ct).ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var klass = TryGetString(root, "class");
            var title = TryGetString(root, "title");
            var address = TryGetString(root, "address");
            var pidValue = root.TryGetProperty("pid", out var pidProp) && pidProp.ValueKind == JsonValueKind.Number
                ? pidProp.GetInt32()
                : 0;

            // Match the X11/xdotool path: /proc/PID/comm gives the
            // process-binary identity that user profiles built up against.
            // See GnomeWindowCallsProvider for the full rationale.
            var rawIdentity = pidValue > 0 ? TryReadProcComm(pidValue) : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
                rawIdentity = klass;
            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                ProcessName: string.IsNullOrWhiteSpace(processName) ? null : processName,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                WindowId: string.IsNullOrWhiteSpace(address) ? null : address,
                AppId: string.IsNullOrWhiteSpace(klass) ? null : klass,
                Source: Name,
                IsTrusted: true);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static string? TryReadProcComm(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/comm";
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }
}
