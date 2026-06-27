using System.ComponentModel;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Sway / i3 active-window provider. Gated on the <c>SWAYSOCK</c>
///     environment variable, which Sway exports for every client inside its
///     session. Walks the JSON tree returned by <c>swaymsg -t get_tree</c> and
///     returns the node where <c>focused: true</c>. Wayland clients expose
///     <c>app_id</c>; XWayland clients fall back to
///     <c>window_properties.class</c>.
/// </summary>
public sealed class SwayActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "sway";

    public bool IsApplicable()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK"))
            && DesktopDetector.BinaryExists("swaymsg");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await ProviderProcessRunner
                .RunAsync("swaymsg", "-t get_tree", ct)
                .ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            var focused = FindFocusedNode(doc.RootElement);
            if (focused is null)
            {
                return null;
            }

            var node = focused.Value;
            var appId = TryGetString(node, "app_id");
            string? xClass = null;
            if (
                node.TryGetProperty("window_properties", out var wp)
                && wp.ValueKind == JsonValueKind.Object
            )
            {
                xClass = TryGetString(wp, "class");
            }

            // /proc/PID/comm gives the binary identity user profiles match against
            // (consistent with the X11/xdotool path; see GnomeWindowCallsProvider).
            int? pidValue = null;
            if (
                node.TryGetProperty("pid", out var pidProp)
                && pidProp.ValueKind == JsonValueKind.Number
                && pidProp.TryGetInt32(out var pidInt)
            )
            {
                pidValue = pidInt;
            }

            var rawIdentity = pidValue is > 0
                ? await TryReadProcCommAsync(pidValue.Value, ct).ConfigureAwait(false)
                : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
            {
                rawIdentity = !string.IsNullOrWhiteSpace(appId) ? appId : xClass;
            }

            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;
            var title = TryGetString(node, "name");
            string? windowId = null;
            if (
                node.TryGetProperty("id", out var idProp)
                && idProp.ValueKind == JsonValueKind.Number
            )
            {
                windowId = idProp.GetInt64().ToString();
            }

            return new ActiveWindowSnapshot(
                string.IsNullOrWhiteSpace(processName) ? null : processName,
                string.IsNullOrWhiteSpace(title) ? null : title,
                windowId,
                string.IsNullOrWhiteSpace(appId) ? null : appId,
                Name
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            // Process.Start throws Win32Exception on missing executables even on Linux.
            return null;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"SwayActiveWindowProvider: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"SwayActiveWindowProvider: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private static JsonElement? FindFocusedNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (
            node.TryGetProperty("focused", out var focused)
            && focused.ValueKind == JsonValueKind.True
        )
        {
            return node;
        }

        if (node.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in nodes.EnumerateArray())
            {
                var match = FindFocusedNode(child);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        // Focused floating windows appear only in floating_nodes, not nodes —
        // both arrays must be searched at every container level.
        if (
            !node.TryGetProperty("floating_nodes", out var floating)
            || floating.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }

        foreach (var child in floating.EnumerateArray())
        {
            var match = FindFocusedNode(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static async Task<string?> TryReadProcCommAsync(int pid, CancellationToken ct)
    {
        try
        {
            var path = $"/proc/{pid}/comm";
            return !File.Exists(path)
                ? null
                : (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return null;
        }
    }
}