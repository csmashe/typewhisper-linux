using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// Sway helper. Same shape as <see cref="HyprlandShortcutWriter"/> but
/// the config syntax differs and the runtime-apply step uses
/// <c>swaymsg reload</c> (Sway has no per-bind keyword-apply IPC the way
/// Hyprland does, so reloading the whole config is the simplest robust
/// path).
/// </summary>
public sealed class SwayShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "sway";
    public string DisplayName => "Sway";
    public bool SupportsPushToTalk => true;

    public bool IsCurrentDesktop()
    {
        if (DesktopDetector.DetectId() != "sway") return false;
        return DesktopDetector.BinaryExists("swaymsg");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append($"~/.config/sway/config — managed block:\n");
        foreach (var line in BuildManagedLines(spec)) sb.Append("  ").Append(line).Append('\n');
        return sb.ToString();
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        var dir = Path.GetDirectoryName(path)!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { return new DeShortcutWriteResult(false, $"Could not create {dir}: {ex.Message}", Array.Empty<string>()); }

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : string.Empty;
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
            return new DeShortcutWriteResult(false,
                $"Your sway config has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
                Array.Empty<string>());

        var managed = BuildManagedLines(spec).ToList();
        var updated = SentinelBlock.ReplaceOrAppend(existing, managed);
        try
        {
            await AtomicWriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(false, $"Could not write {path}: {ex.Message}", Array.Empty<string>());
        }

        var reloaded = await ReloadAsync(ct).ConfigureAwait(false);
        var message = "Sway shortcut installed in ~/.config/sway/config";
        string? warning = reloaded ? null : "Config written, but `swaymsg reload` failed. Reload Sway manually to pick up the binding.";
        return new DeShortcutWriteResult(true, message, new[] { path }, warning);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
            return new DeShortcutWriteResult(true, "No sway config to update.", Array.Empty<string>());

        var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
            return new DeShortcutWriteResult(false,
                $"Your sway config has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
                Array.Empty<string>());
        if (scan.OpenLine is null)
            return new DeShortcutWriteResult(true, "No Sway integration to remove.", Array.Empty<string>());

        var updated = SentinelBlock.Remove(existing);
        try
        {
            await AtomicWriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(false, $"Could not write {path}: {ex.Message}", Array.Empty<string>());
        }

        var reloaded = await ReloadAsync(ct).ConfigureAwait(false);
        var warning = reloaded ? null : "Block removed, but `swaymsg reload` failed. Reload Sway manually to drop the live bindings.";
        return new DeShortcutWriteResult(true, "Sway managed block removed.", new[] { path }, warning);
    }

    private static IEnumerable<string> BuildManagedLines(DeShortcutSpec spec)
    {
        var trigger = ToSwayBind(spec.Trigger);
        // --no-repeat keeps a held key from spamming `record start`
        // dozens of times a second when the user uses PTT. Sway will
        // still deliver a single press + a single release.
        yield return $"bindsym --no-repeat {trigger} exec {spec.OnPressCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnReleaseCommand))
            yield return $"bindsym --release {trigger} exec {spec.OnReleaseCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnCancelTrigger) && !string.IsNullOrWhiteSpace(spec.OnCancelCommand))
            yield return $"bindsym {ToSwayBind(spec.OnCancelTrigger!)} exec {spec.OnCancelCommand}";
    }

    /// <summary>
    /// Convert "Ctrl+Shift+Space" into Sway's "Ctrl+Shift+space" form.
    /// Sway is case-sensitive for the named key tail (lower-case for
    /// "space", "escape", etc.) and uses "+" between tokens.
    /// </summary>
    public static string ToSwayBind(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return string.Empty;
        var parts = trigger.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            var mapped = p switch
            {
                "ctrl" or "control" => "Ctrl",
                "shift" => "Shift",
                "alt" or "meta" => "Alt",
                "super" or "win" or "windows" or "cmd" => "Mod4",
                _ => parts[i],
            };
            if (sb.Length > 0) sb.Append('+');
            sb.Append(mapped);
        }

        var tail = parts[^1];
        if (sb.Length > 0) sb.Append('+');
        // Sway key names like "space", "Return", "Escape" come from
        // xkbcommon — keys longer than a single character are lower-
        // cased to match the xkbcommon convention. Function keys are
        // the exception: xkbcommon's keysym is "F1".."F35" mixed-case.
        sb.Append(NormalizeSwayKey(tail));
        return sb.ToString();
    }

    private static string NormalizeSwayKey(string key)
    {
        if (key.Length <= 1) return key;
        if (IsFunctionKey(key)) return "F" + key.Substring(1);
        return key.ToLowerInvariant();
    }

    private static bool IsFunctionKey(string k)
    {
        if (k.Length < 2 || (k[0] != 'F' && k[0] != 'f')) return false;
        for (var i = 1; i < k.Length; i++)
            if (!char.IsDigit(k[i])) return false;
        return true;
    }

    private static string ResolveConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
        return Path.Combine(configHome, "sway", "config");
    }

    private static async Task<bool> ReloadAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("swaymsg")) return false;
        var (ok, _, _) = await RunAsync("swaymsg", new[] { "reload" }, ct).ConfigureAwait(false);
        return ok;
    }

    private static async Task AtomicWriteAsync(string target, string contents, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.tmp");
        await File.WriteAllTextAsync(tmp, contents, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite: true);
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, string.Empty, $"Could not start {fileName}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
