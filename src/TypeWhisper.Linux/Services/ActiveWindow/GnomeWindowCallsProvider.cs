using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// GNOME Wayland active-window provider backed by the user-installed
/// "Window Calls" GNOME Shell extension
/// (<c>window-calls@domandoman.xyz</c>). The extension exports
/// <c>org.gnome.Shell.Extensions.Windows</c> on the session bus, which
/// sidesteps the modern <c>org.gnome.Shell.Introspect.GetWindows</c>
/// AccessDenied policy.
///
/// This provider sits *before* <see cref="GnomeShellActiveWindowProvider"/>
/// in the chain: when the extension is installed it's the fast, reliable
/// path; when it isn't, the call fails fast (UnknownMethod / ServiceUnknown
/// in &lt; 30 ms) and the chain falls through.
/// </summary>
public sealed class GnomeWindowCallsProvider : IActiveWindowProvider
{
    private const string DBusDest = "org.gnome.Shell";
    private const string DBusPath = "/org/gnome/Shell/Extensions/Windows";
    private const string DBusInterface = "org.gnome.Shell.Extensions.Windows";

    public string Name => "gnome-window-calls";

    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var lower = raw.ToLowerInvariant();
        if (!lower.Contains("gnome") && !lower.Contains("ubuntu")) return false;
        return DesktopDetector.BinaryExists("gdbus");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (listExit, listOutput) = await ProviderProcessRunner.RunAsync(
                "gdbus",
                $"call --session --dest {DBusDest} --object-path {DBusPath} --method {DBusInterface}.List",
                ct).ConfigureAwait(false);

            if (listExit != 0 || string.IsNullOrWhiteSpace(listOutput))
                return null;

            var focused = ParseFocusedWindow(listOutput);
            if (focused is null)
                return null;

            // Prefer /proc/PID/comm so the ProcessName matches the X11
            // path (xdotool → getwindowpid → Process.GetProcessById). User
            // profiles built up against X11 list comm-style names like
            // "soffice.bin", "firefox", "ghostty" — using the wm_class
            // identity here would silently break those profiles on
            // Wayland. wm_class is kept as a fallback for the rare case
            // where /proc/PID/comm isn't readable, and continues to live
            // on Snapshot.AppId for any caller that wants the Wayland
            // identity directly.
            var rawIdentity = focused.Value.Pid is > 0
                ? TryReadProcComm(focused.Value.Pid.Value)
                : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
                rawIdentity = focused.Value.WmClass;
            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                ProcessName: string.IsNullOrWhiteSpace(processName) ? null : processName,
                Title: string.IsNullOrWhiteSpace(focused.Value.Title) ? null : focused.Value.Title,
                WindowId: focused.Value.WindowId,
                AppId: string.IsNullOrWhiteSpace(focused.Value.WmClass) ? null : focused.Value.WmClass,
                Source: Name,
                IsTrusted: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the focused window out of the gvariant-wrapped JSON returned
    /// by <c>Windows.List()</c>. Returns null when no window has
    /// <c>focus: true</c> (transient state during workspace switches) or
    /// when the payload is malformed.
    /// </summary>
    internal static FocusedWindow? ParseFocusedWindow(string gvariantOutput)
    {
        var json = UnwrapGvariantString(gvariantOutput);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var window in doc.RootElement.EnumerateArray())
            {
                if (window.ValueKind != JsonValueKind.Object) continue;

                // Real Window Calls payload uses "has_focus". Some forks
                // shipped "focus" — accept either so we don't drift if a
                // user has the older variant installed.
                if (!IsFocused(window, "has_focus") && !IsFocused(window, "focus"))
                    continue;

                var wmClass = TryGetString(window, "wm_class")
                              ?? TryGetString(window, "wm_class_instance");
                var pid = TryGetInt(window, "pid");
                var title = TryGetString(window, "title");
                string? id = null;
                if (window.TryGetProperty("id", out var idProp))
                {
                    id = idProp.ValueKind switch
                    {
                        JsonValueKind.Number => idProp.GetInt64().ToString(),
                        JsonValueKind.String => idProp.GetString(),
                        _ => null,
                    };
                }

                return new FocusedWindow(wmClass, pid, id, title);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Strips the gvariant tuple wrapper (<c>('...',)</c>) and unescapes
    /// the inner single-quoted string. gdbus output escapes embedded
    /// single quotes as <c>\'</c> and literal backslashes as <c>\\</c>
    /// inside that string. We pass everything else through so the embedded
    /// JSON keeps its own backslash escapes (<c>\n</c>, <c>\"</c>, ...).
    /// </summary>
    internal static string? UnwrapGvariantString(string gvariantOutput)
    {
        var trimmed = gvariantOutput.Trim();
        if (trimmed.Length < 4 || trimmed[0] != '(' || trimmed[^1] != ')')
            return null;

        var inner = trimmed[1..^1].Trim();
        if (inner.EndsWith(','))
            inner = inner[..^1].TrimEnd();

        if (inner.Length < 2 || inner[0] != '\'' || inner[^1] != '\'')
            return null;

        var body = inner[1..^1];
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                if (next == '\\' || next == '\'')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
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

    private static bool IsFocused(JsonElement window, string key)
    {
        return window.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.True;
    }

    internal readonly record struct FocusedWindow(string? WmClass, int? Pid, string? WindowId, string? Title);
}
