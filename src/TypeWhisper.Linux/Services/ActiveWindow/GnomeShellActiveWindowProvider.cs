using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// GNOME Shell active-window provider. Gated on <c>XDG_CURRENT_DESKTOP</c>
/// containing "GNOME" or "ubuntu". Talks to the
/// <c>org.gnome.Shell.Introspect</c> session-bus interface via
/// <c>gdbus</c> and parses the dict-of-dicts gvariant payload for the
/// window with <c>has-focus: true</c>. When introspection is disabled
/// (<c>gsettings set org.gnome.shell introspect true</c>) the
/// <c>Source</c> stays "gnome-shell" so the failure tracker can surface
/// the right remediation.
/// </summary>
public sealed class GnomeShellActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "gnome-shell";

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
            var (exit, output) = await ProviderProcessRunner.RunAsync(
                "gdbus",
                "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows",
                ct).ConfigureAwait(false);

            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ParseFocusedWindow(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal ActiveWindowSnapshot? ParseFocusedWindow(string gvariantOutput)
    {
        foreach (var window in EnumerateWindows(gvariantOutput))
        {
            if (!TryReadBool(window.Body, "has-focus", out var focused) || !focused)
                continue;

            var appId = TryReadString(window.Body, "app-id");
            var wmClass = TryReadString(window.Body, "wm-class");
            var title = TryReadString(window.Body, "title");
            var pid = TryReadInt(window.Body, "pid");

            // Prefer /proc/PID/comm so the ProcessName matches the X11
            // path. See GnomeWindowCallsProvider for the full rationale.
            var rawIdentity = pid is > 0 ? TryReadProcComm(pid.Value) : null;
            if (string.IsNullOrWhiteSpace(rawIdentity))
                rawIdentity = wmClass;
            var processName = !string.IsNullOrWhiteSpace(rawIdentity)
                ? ProcessNameNormalizer.Normalize(rawIdentity).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                ProcessName: processName,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                WindowId: window.Id,
                AppId: string.IsNullOrWhiteSpace(appId) ? null : appId,
                Source: Name,
                IsTrusted: true);
        }

        return null;
    }

    private static IEnumerable<(string Id, string Body)> EnumerateWindows(string output)
    {
        // gdbus prints something like:
        //   ({uint64 12345: {'app-id': <'Code'>, 'wm-class': <'code'>,
        //                    'title': <'main.cs'>, 'has-focus': <true>, ...},
        //     uint64 67890: { ... }},)
        // We only need to find each "uint64 N: { ... }" pair. The inner
        // braces don't nest beyond one level, so a depth counter is enough.
        var i = 0;
        while (i < output.Length)
        {
            var idMatch = Regex.Match(output[i..], @"uint64\s+(\d+)\s*:\s*\{");
            if (!idMatch.Success) yield break;

            var id = idMatch.Groups[1].Value;
            var bodyStart = i + idMatch.Index + idMatch.Length;
            var depth = 1;
            var j = bodyStart;
            var inString = false;
            var stringQuote = '\0';
            while (j < output.Length && depth > 0)
            {
                var c = output[j];
                if (inString)
                {
                    if (c == '\\' && j + 1 < output.Length)
                    {
                        j += 2;
                        continue;
                    }
                    if (c == stringQuote)
                        inString = false;
                }
                else
                {
                    if (c == '\'' || c == '"')
                    {
                        inString = true;
                        stringQuote = c;
                    }
                    else if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                j++;
            }

            if (depth != 0) yield break;
            yield return (id, output.Substring(bodyStart, j - bodyStart - 1));
            i = j;
        }
    }

    private static string? TryReadString(string body, string key)
    {
        var pattern = $@"'{Regex.Escape(key)}'\s*:\s*<'((?:[^'\\]|\\.)*)'>";
        var m = Regex.Match(body, pattern);
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }

    private static bool TryReadBool(string body, string key, out bool value)
    {
        value = false;
        var pattern = $@"'{Regex.Escape(key)}'\s*:\s*<(true|false)>";
        var m = Regex.Match(body, pattern);
        if (!m.Success) return false;
        value = m.Groups[1].Value == "true";
        return true;
    }

    private static int? TryReadInt(string body, string key)
    {
        // gdbus emits integers inside the boxed variant as
        //   'pid': <uint32 1234>  or  'pid': <1234>
        // depending on the underlying signature. The leading type token is
        // optional, so the regex tolerates both shapes.
        var pattern = $@"'{Regex.Escape(key)}'\s*:\s*<\s*(?:[a-z0-9]+\s+)?(\d+)\s*>";
        var m = Regex.Match(body, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
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
