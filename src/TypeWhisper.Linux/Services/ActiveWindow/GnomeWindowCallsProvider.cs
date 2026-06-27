using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     GNOME Wayland active-window provider backed by the "Window Calls"
///     extension (<c>window-calls@domandoman.xyz</c>), which exports
///     <c>org.gnome.Shell.Extensions.Windows</c> to sidestep the modern
///     <c>GetWindows</c> AccessDenied policy. Sits before
///     <see cref="GnomeShellActiveWindowProvider" /> in the chain; fails fast
///     (&lt;30 ms) when the extension is absent.
/// </summary>
public sealed class GnomeWindowCallsProvider : IActiveWindowProvider
{
    private const string DBusDest = "org.gnome.Shell";

    // Both the original "Window Calls" (window-calls@domandoman.xyz) and its
    // fork "Window Calls Extended" (window-calls-extended@hseliger.eu) export a
    // compatible List method returning the same window JSON — just at different
    // object paths/interfaces. Try each so whichever the user installed works.
    private static readonly (string Path, string Interface)[] Endpoints =
    [
        ("/org/gnome/Shell/Extensions/Windows", "org.gnome.Shell.Extensions.Windows"),
        ("/org/gnome/Shell/Extensions/WindowsExt", "org.gnome.Shell.Extensions.WindowsExt")
    ];

    public string Name => "gnome-window-calls";

    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var lower = raw.ToLowerInvariant();
        if (!lower.Contains("gnome") && !lower.Contains("ubuntu"))
        {
            return false;
        }

        return DesktopDetector.BinaryExists("gdbus");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            string? listOutput = null;
            foreach (var (path, iface) in Endpoints)
            {
                var (exit, output) = await ProviderProcessRunner
                    .RunAsync(
                        "gdbus",
                        $"call --session --dest {DBusDest} --object-path {path} --method {iface}.List",
                        ct
                    )
                    .ConfigureAwait(false);
                if (exit != 0 || string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                listOutput = output;
                break;
            }

            if (string.IsNullOrWhiteSpace(listOutput))
            {
                return null;
            }

            var focused = ParseFocusedWindow(listOutput);
            if (focused is null)
            {
                return null;
            }

            // Prefer /proc/PID/comm so ProcessName matches the X11 path
            // (xdotool → comm-style names like "firefox", "soffice.bin").
            // Using wm_class here would silently break profiles built on X11.
            // wm_class is kept as a fallback and is also exposed as Snapshot.AppId.
            var rawIdentity = focused.Value.Pid is > 0
                ? TryReadProcComm(focused.Value.Pid.Value)
                : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
            {
                rawIdentity = focused.Value.WmClass;
            }

            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                string.IsNullOrWhiteSpace(processName) ? null : processName,
                string.IsNullOrWhiteSpace(focused.Value.Title) ? null : focused.Value.Title,
                focused.Value.WindowId,
                string.IsNullOrWhiteSpace(focused.Value.WmClass)
                    ? null
                    : focused.Value.WmClass,
                Name
            );
        }
        catch
        {
            // Includes OperationCanceledException — treat as a miss so the
            // chain falls through to GnomeShellActiveWindowProvider.
            return null;
        }
    }

    /// <summary>
    ///     Parses the focused window from the gvariant-wrapped JSON returned by
    ///     <c>Windows.List()</c>. Returns null when no window has focus (e.g.
    ///     during workspace switches) or when the payload is malformed.
    /// </summary>
    internal static FocusedWindow? ParseFocusedWindow(string gvariantOutput)
    {
        var json = UnwrapGvariantString(gvariantOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var window in doc.RootElement.EnumerateArray())
            {
                if (window.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Real Window Calls payload uses "has_focus". Some forks
                // shipped "focus" — accept either so we don't drift if a
                // user has the older variant installed.
                if (!IsFocused(window, "has_focus") && !IsFocused(window, "focus"))
                {
                    continue;
                }

                var wmClass =
                    TryGetString(window, "wm_class") ?? TryGetString(window, "wm_class_instance");
                var pid = TryGetInt(window, "pid");
                var title = TryGetString(window, "title");
                string? id = null;
                if (window.TryGetProperty("id", out var idProp))
                {
                    id = idProp.ValueKind switch
                    {
                        JsonValueKind.Number => idProp.GetInt64().ToString(),
                        JsonValueKind.String => idProp.GetString(),
                        _ => null
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
    ///     Strips the gvariant tuple wrapper (<c>('...',)</c>) and unescapes the
    ///     inner single-quoted string. gdbus escapes <c>\'</c> and <c>\\</c>;
    ///     everything else passes through so embedded JSON escapes are preserved.
    /// </summary>
    internal static string? UnwrapGvariantString(string gvariantOutput)
    {
        var trimmed = gvariantOutput.Trim();
        if (trimmed.Length < 4 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            return null;
        }

        var inner = trimmed[1..^1].Trim();
        if (inner.EndsWith(','))
        {
            inner = inner[..^1].TrimEnd();
        }

        if (inner.Length < 2 || inner[0] != '\'' || inner[^1] != '\'')
        {
            return null;
        }

        var body = inner[1..^1];
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                if (next is '\\' or '\'')
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
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
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

    private static bool IsFocused(JsonElement window, string key)
    {
        return window.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.True;
    }

    internal readonly record struct FocusedWindow(
        string? WmClass,
        int? Pid,
        string? WindowId,
        string? Title
    );
}