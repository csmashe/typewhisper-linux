using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// Sway / i3 active-window provider. Gated on the <c>SWAYSOCK</c>
/// environment variable, which Sway exports for every client inside its
/// session. Walks the JSON tree returned by <c>swaymsg -t get_tree</c> and
/// returns the node where <c>focused: true</c>. Wayland clients expose
/// <c>app_id</c>; XWayland clients fall back to
/// <c>window_properties.class</c>.
/// </summary>
public sealed class SwayActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "sway";

    public bool IsApplicable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")))
            return false;
        return DesktopDetector.BinaryExists("swaymsg");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await ProviderProcessRunner
                .RunAsync("swaymsg", "-t get_tree", ct).ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            using var doc = JsonDocument.Parse(output);
            var focused = FindFocusedNode(doc.RootElement);
            if (focused is null)
                return null;

            var node = focused.Value;
            var appId = TryGetString(node, "app_id");
            string? xClass = null;
            if (node.TryGetProperty("window_properties", out var wp) && wp.ValueKind == JsonValueKind.Object)
                xClass = TryGetString(wp, "class");

            // Match the X11/xdotool path: /proc/PID/comm gives the
            // process-binary identity that user profiles built up against.
            // See GnomeWindowCallsProvider for the full rationale.
            int? pidValue = null;
            if (node.TryGetProperty("pid", out var pidProp) && pidProp.ValueKind == JsonValueKind.Number
                && pidProp.TryGetInt32(out var pidInt))
                pidValue = pidInt;
            var rawIdentity = pidValue is > 0 ? TryReadProcComm(pidValue.Value) : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
                rawIdentity = !string.IsNullOrWhiteSpace(appId) ? appId : xClass;
            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;
            var title = TryGetString(node, "name");
            string? windowId = null;
            if (node.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                windowId = idProp.GetInt64().ToString();

            return new ActiveWindowSnapshot(
                ProcessName: string.IsNullOrWhiteSpace(processName) ? null : processName,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                WindowId: windowId,
                AppId: string.IsNullOrWhiteSpace(appId) ? null : appId,
                Source: Name,
                IsTrusted: true);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? FindFocusedNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        if (node.TryGetProperty("focused", out var focused)
            && focused.ValueKind == JsonValueKind.True)
        {
            return node;
        }

        if (node.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in nodes.EnumerateArray())
            {
                var match = FindFocusedNode(child);
                if (match is not null) return match;
            }
        }

        if (node.TryGetProperty("floating_nodes", out var floating) && floating.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in floating.EnumerateArray())
            {
                var match = FindFocusedNode(child);
                if (match is not null) return match;
            }
        }

        return null;
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
