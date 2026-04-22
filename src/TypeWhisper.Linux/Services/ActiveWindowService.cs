using System.Diagnostics;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux active-window lookup via xdotool (X11 only). On Wayland there's no
/// cross-compositor equivalent — callers should treat nulls as "unknown" and
/// degrade gracefully.
/// </summary>
public sealed class ActiveWindowService : IActiveWindowService
{
    private static readonly bool IsXdotoolAvailable = CheckXdotoolAvailable();
    private static readonly bool IsXclipAvailable = CheckCommandAvailable("xclip", "-version");
    private static readonly bool IsBusctlAvailable = CheckCommandAvailable("busctl", "--version");
    private static readonly bool IsGdbusAvailable = CheckCommandAvailable("gdbus", "--version");
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "firefox", "waterfox"
    };
    private static readonly string[] BrowserAppNameHints =
    [
        "google chrome",
        "chrome",
        "microsoft edge",
        "edge",
        "brave",
        "opera",
        "vivaldi",
        "chromium",
        "firefox",
        "waterfox"
    ];
    private const string AtSpiRegistryBusName = "org.a11y.atspi.Registry";
    private const string AtSpiRootPath = "/org/a11y/atspi/accessible/root";
    private const int AtSpiStateActive = 1;
    private const int AtSpiStateFocused = 11;
    private const int AtSpiStateEditable = 18;
    private const int AtSpiStateShowing = 25;
    private const int AtSpiStateVisible = 30;
    private const int AtSpiRoleFrame = 23;
    private const int AtSpiRoleWindow = 69;
    private const int AtSpiRoleEditBar = 77;
    private const int AtSpiRoleEntry = 79;

    private string? _lastWindowId;
    private string? _lastTitle;
    private string? _cachedUrl;

    public string? GetActiveWindowProcessName()
    {
        if (!IsXdotoolAvailable) return null;
        var pid = RunXdotool("getactivewindow getwindowpid");
        if (string.IsNullOrWhiteSpace(pid) || !int.TryParse(pid, out var pidInt))
            return null;

        try
        {
            using var proc = Process.GetProcessById(pidInt);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetActiveWindowTitle()
    {
        if (!IsXdotoolAvailable) return null;
        var title = RunXdotool("getactivewindow getwindowname");
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    public string? GetBrowserUrl()
    {
        var windowId = IsXdotoolAvailable ? RunXdotool("getactivewindow") : null;
        var title = GetActiveWindowTitle();

        if (!string.IsNullOrWhiteSpace(windowId)
            && windowId == _lastWindowId
            && title == _lastTitle
            && _cachedUrl is not null)
        {
            return _cachedUrl;
        }

        _lastWindowId = windowId;
        _lastTitle = title;
        _cachedUrl = null;

        var atSpiUrl = TryGetBrowserUrlViaAtSpi();
        if (atSpiUrl is not null)
            return _cachedUrl = atSpiUrl;

        if (!string.IsNullOrWhiteSpace(windowId)
            && IsXclipAvailable
            && IsSupportedBrowserProcess(GetActiveWindowProcessName()))
        {
            return _cachedUrl = TryCaptureBrowserUrl(windowId);
        }

        return null;
    }

    public IReadOnlyList<string> GetRunningAppProcessNames()
    {
        try
        {
            var ownId = Environment.ProcessId;
            return Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.Id != ownId && !string.IsNullOrWhiteSpace(p.MainWindowTitle); }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return null; }
                })
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return [];
        }
    }

    private static string? TryCaptureBrowserUrl(string windowId)
    {
        string? previousClipboard = null;

        try
        {
            previousClipboard = TryReadClipboardText();

            // Clear first so a failed copy does not return stale clipboard contents.
            if (!TryWriteClipboardText(string.Empty))
                return null;

            if (!SendBrowserAddressBarCaptureKeys(windowId))
                return null;

            var copied = TryReadClipboardText();
            return SanitizeCapturedBrowserUrl(copied);
        }
        finally
        {
            if (previousClipboard is not null)
                TryWriteClipboardText(previousClipboard);
        }
    }

    private static string? TryGetBrowserUrlViaAtSpi()
    {
        if (!IsBusctlAvailable || !IsGdbusAvailable)
            return null;

        var address = GetAtSpiBusAddress();
        if (string.IsNullOrWhiteSpace(address))
            return null;

        foreach (var app in GetAccessibleChildren(address, new AccessibleRef(AtSpiRegistryBusName, AtSpiRootPath)))
        {
            var appName = GetAccessibleName(address, app);
            if (!IsSupportedBrowserIdentity(appName))
                continue;

            var activeWindow = FindActiveBrowserWindow(address, app);
            if (activeWindow is null)
                continue;

            var url = FindLikelyBrowserUrlInSubtree(address, activeWindow.Value);
            if (url is not null)
                return url;
        }

        return null;
    }

    private static string? GetAtSpiBusAddress()
    {
        var exitCode = RunProcess(
            "gdbus",
            "call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress",
            out var output);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(output, @"\('(?<value>.+)'\s*,?\)");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static AccessibleRef? FindActiveBrowserWindow(string address, AccessibleRef app)
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((app, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > 3)
                continue;

            var role = GetAccessibleRole(address, node);
            var states = GetAccessibleState(address, node);
            if ((role == AtSpiRoleFrame || role == AtSpiRoleWindow) && HasState(states, AtSpiStateActive))
                return node;

            foreach (var child in GetAccessibleChildren(address, node))
                queue.Enqueue((child, depth + 1));
        }

        return null;
    }

    private static string? FindLikelyBrowserUrlInSubtree(string address, AccessibleRef root)
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((root, 0));

        var seen = 0;
        string? bestUrl = null;
        var bestScore = int.MinValue;

        while (queue.Count > 0 && seen < 500)
        {
            var (node, depth) = queue.Dequeue();
            seen++;
            if (depth > 8)
                continue;

            var role = GetAccessibleRole(address, node);
            var states = GetAccessibleState(address, node);
            if (!HasState(states, AtSpiStateShowing) || !HasState(states, AtSpiStateVisible))
                continue;

            var name = GetAccessibleName(address, node);
            var interfaces = GetAccessibleInterfaces(address, node);
            var candidate = TryGetAccessibleText(address, node, interfaces) ?? name;
            var score = ScoreBrowserUrlCandidate(role, states, name, candidate, interfaces);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = SanitizeCapturedBrowserUrl(candidate);
            }

            foreach (var child in GetAccessibleChildren(address, node))
                queue.Enqueue((child, depth + 1));
        }

        return bestUrl;
    }

    private static string? TryGetAccessibleText(string address, AccessibleRef node, IReadOnlyList<string> interfaces)
    {
        if (interfaces.Contains("org.a11y.atspi.Value", StringComparer.Ordinal))
        {
            var valueText = GetBusctlStringProperty(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Value", "Text");
            if (!string.IsNullOrWhiteSpace(valueText))
                return valueText;
        }

        if (!interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
            return null;

        var characterCount = GetBusctlUInt32Property(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Text", "CharacterCount");
        if (characterCount <= 0)
            return null;

        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Text",
            "GetText",
            "ii",
            "0",
            characterCount.ToString());

        return ParseFirstQuotedString(output);
    }

    private static IReadOnlyList<AccessibleRef> GetAccessibleChildren(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetChildren");
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var values = ParseQuotedStrings(output);
        var children = new List<AccessibleRef>(values.Count / 2);
        for (var i = 0; i + 1 < values.Count; i += 2)
            children.Add(new AccessibleRef(values[i], values[i + 1]));

        return children;
    }

    private static IReadOnlyList<string> GetAccessibleInterfaces(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetInterfaces");
        return string.IsNullOrWhiteSpace(output) ? [] : ParseQuotedStrings(output);
    }

    private static string? GetAccessibleName(string address, AccessibleRef node) =>
        GetBusctlStringProperty(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "Name");

    private static int GetAccessibleRole(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetRole");
        return ParseLastInt(output);
    }

    private static IReadOnlyList<uint> GetAccessibleState(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetState");
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var ints = Regex.Matches(output, @"\b\d+\b")
            .Select(match => uint.Parse(match.Value))
            .ToList();

        return ints.Count > 1 ? ints[1..] : [];
    }

    private static string? GetBusctlStringProperty(string address, string destination, string path, string @interface, string property)
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseFirstQuotedString(output);
    }

    private static int GetBusctlUInt32Property(string address, string destination, string path, string @interface, string property)
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseLastInt(output);
    }

    private static string? RunBusctlCall(
        string address,
        string destination,
        string path,
        string @interface,
        string method,
        params string[] signatureAndArgs)
    {
        var args = new List<string>
        {
            $"--address={address}",
            "call",
            destination,
            path,
            @interface,
            method
        };
        args.AddRange(signatureAndArgs);

        var exitCode = RunProcess("busctl", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static string? RunBusctlGetProperty(string address, string destination, string path, string @interface, string property)
    {
        var exitCode = RunProcess(
            "busctl",
            [$"--address={address}", "get-property", destination, path, @interface, property],
            out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static bool SendBrowserAddressBarCaptureKeys(string windowId)
    {
        // Linux adaptation: browsers reliably expose Ctrl+L / Ctrl+C on X11,
        // so we can capture the address bar without adding a full AT-SPI stack.
        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+l"))
            return false;

        Thread.Sleep(60);

        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+c"))
            return false;

        Thread.Sleep(80);

        RunXdotoolKey(windowId, "key Escape");
        return true;
    }

    private static bool RunXdotoolKey(string windowId, string args)
    {
        var exitCode = RunProcess("xdotool", $"windowactivate --sync {windowId} {args}", out _);
        return exitCode == 0;
    }

    private static string? TryReadClipboardText()
    {
        var exitCode = RunProcess("xclip", "-selection clipboard -o", out var output);
        return exitCode == 0 ? output : null;
    }

    private static bool TryWriteClipboardText(string text)
    {
        var exitCode = RunProcessWithInput("xclip", "-selection clipboard", text);
        return exitCode == 0;
    }

    internal static bool IsSupportedBrowserProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && BrowserProcessNames.Contains(processName);

    internal static bool IsSupportedBrowserIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;

        if (IsSupportedBrowserProcess(identity))
            return true;

        return BrowserAppNameHints.Any(hint => identity.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? SanitizeCapturedBrowserUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !IsLikelyUrl(trimmed))
            return null;

        return NormalizeUrl(trimmed);
    }

    internal static bool IsLikelyUrl(string value)
    {
        if (value.Length < 3 || value.Length > 2048)
            return false;

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Contains(' ') || !value.Contains('.'))
            return false;

        var host = value.Split('/')[0];
        return host.Contains('.');
    }

    internal static string NormalizeUrl(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return value;

        return "https://" + value;
    }

    internal static bool HasState(IReadOnlyList<uint> stateWords, int state)
    {
        var wordIndex = state / 32;
        var bitOffset = state % 32;
        return wordIndex < stateWords.Count && (stateWords[wordIndex] & (1u << bitOffset)) != 0;
    }

    internal static int ScoreBrowserUrlCandidate(
        int role,
        IReadOnlyList<uint> states,
        string? name,
        string? candidateText,
        IReadOnlyList<string> interfaces)
    {
        var sanitized = SanitizeCapturedBrowserUrl(candidateText);
        if (sanitized is null)
            return int.MinValue;

        var score = 100;
        if (role == AtSpiRoleEditBar)
            score += 120;
        else if (role == AtSpiRoleEntry)
            score += 80;

        if (HasState(states, AtSpiStateFocused))
            score += 50;
        if (HasState(states, AtSpiStateEditable))
            score += 15;
        if (interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
            score += 10;
        if (sanitized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            score += 10;
        if (sanitized.Contains('/', StringComparison.Ordinal))
            score += 5;
        if (!string.IsNullOrWhiteSpace(name) && name.Contains("address", StringComparison.OrdinalIgnoreCase))
            score += 40;

        return score;
    }

    private static bool CheckXdotoolAvailable() => CheckCommandAvailable("xdotool", "--version");

    private static bool CheckCommandAvailable(string command, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunXdotool(string args)
    {
        var exitCode = RunProcess("xdotool", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static int RunProcess(string fileName, string args, out string? output)
    {
        output = null;

        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int RunProcess(string fileName, IReadOnlyList<string> args, out string? output)
    {
        output = null;

        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var p = Process.Start(startInfo);

            if (p is null)
                return -1;

            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int RunProcessWithInput(string fileName, string args, string input)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;

            p.StandardInput.Write(input);
            p.StandardInput.Close();
            p.WaitForExit(1000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static List<string> ParseQuotedStrings(string value) =>
        Regex.Matches(value, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .ToList();

    private static string? ParseFirstQuotedString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var values = ParseQuotedStrings(value);
        return values.Count > 0 ? values[0] : null;
    }

    private static int ParseLastInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var match = Regex.Matches(value, @"\b\d+\b").LastOrDefault();
        return match is null ? 0 : int.Parse(match.Value);
    }
}

internal readonly record struct AccessibleRef(string BusName, string ObjectPath);
