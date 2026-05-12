using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// Hyprland helper. Two things happen on Write:
/// <list type="number">
///   <item>The user's <c>~/.config/hypr/hyprland.conf</c> gets a managed
///   sentinel block appended (or updated in place) with <c>bind</c> +
///   <c>bindr</c> + a cancel bind. This is what survives a session
///   restart.</item>
///   <item>We then call <c>hyprctl keyword bind ...</c> for each line so
///   the binding takes effect immediately, without the user needing to
///   reload Hyprland. If hyprctl fails (binary missing, socket gone,
///   non-running session) the config write is still considered a
///   success — we surface a warning, not an error.</item>
/// </list>
/// </summary>
public sealed class HyprlandShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "hyprland";
    public string DisplayName => "Hyprland";
    public bool SupportsPushToTalk => true;

    public bool IsCurrentDesktop()
    {
        // The presence of HYPRLAND_INSTANCE_SIGNATURE is a stronger
        // signal than XDG_CURRENT_DESKTOP because it's only set inside
        // a live Hyprland session. We additionally require hyprctl to
        // be available so the runtime-bind step has a chance.
        if (DesktopDetector.DetectId() != "hyprland") return false;
        return DesktopDetector.BinaryExists("hyprctl");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append($"~/.config/hypr/hyprland.conf — managed block:\n");
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
                $"Your hyprland.conf has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually (remove the stray sentinel lines) and try again.",
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

        // Runtime apply via hyprctl. We feed the bind / bindr / bind
        // payload one at a time so we can isolate failures and emit a
        // precise warning if only one of them fails. Failures here are
        // non-fatal — the persistent config has already been written.
        var liveOk = await ApplyLiveAsync(spec, ct).ConfigureAwait(false);

        var message = "Hyprland shortcut installed in ~/.config/hypr/hyprland.conf";
        string? warning = liveOk
            ? null
            : "Config written, but `hyprctl` could not apply the bind live. Run `hyprctl reload` (or restart Hyprland) to pick it up.";
        return new DeShortcutWriteResult(true, message, new[] { path }, warning);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
            return new DeShortcutWriteResult(true, "No hyprland.conf to update.", Array.Empty<string>());

        var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
            return new DeShortcutWriteResult(false,
                $"Your hyprland.conf has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
                Array.Empty<string>());
        if (scan.OpenLine is null)
            return new DeShortcutWriteResult(true, "No Hyprland integration to remove.", Array.Empty<string>());

        var updated = SentinelBlock.Remove(existing);
        try
        {
            await AtomicWriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(false, $"Could not write {path}: {ex.Message}", Array.Empty<string>());
        }

        // The runtime side of removal is to unbind, but Hyprland's
        // unbind syntax is finicky and varies across versions. Asking
        // the user to reload is robust and matches what they already
        // expect from compositor config edits.
        return new DeShortcutWriteResult(true,
            "Hyprland managed block removed. Run `hyprctl reload` (or restart Hyprland) to drop the live binding.",
            new[] { path });
    }

    private static IEnumerable<string> BuildManagedLines(DeShortcutSpec spec)
    {
        var (mods, key) = ToHyprlandBind(spec.Trigger);
        yield return $"bind  = {mods}, {key}, exec, {spec.OnPressCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnReleaseCommand))
            yield return $"bindr = {mods}, {key}, exec, {spec.OnReleaseCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnCancelTrigger) && !string.IsNullOrWhiteSpace(spec.OnCancelCommand))
        {
            var (cmods, ckey) = ToHyprlandBind(spec.OnCancelTrigger!);
            yield return $"bind  = {cmods}, {ckey}, exec, {spec.OnCancelCommand}";
        }
    }

    /// <summary>
    /// Convert "Ctrl+Shift+Space" into Hyprland's "CTRL SHIFT", "SPACE"
    /// form. Modifiers are space-separated; the key is uppercased
    /// (Hyprland accepts either case but uppercase reads better in
    /// hand-written configs).
    /// </summary>
    public static (string mods, string key) ToHyprlandBind(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return (string.Empty, string.Empty);
        var parts = trigger.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (string.Empty, string.Empty);

        var mods = new List<string>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            mods.Add(p switch
            {
                "ctrl" or "control" => "CTRL",
                "shift" => "SHIFT",
                "alt" or "meta" => "ALT",
                "super" or "win" or "windows" or "cmd" => "SUPER",
                _ => parts[i].ToUpperInvariant(),
            });
        }

        var key = parts[^1].ToUpperInvariant();
        return (string.Join(' ', mods), key);
    }

    private async Task<bool> ApplyLiveAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("hyprctl")) return false;
        var anyFailed = false;
        foreach (var line in BuildManagedLines(spec))
        {
            // Strip the leading "bind  = " / "bindr = " — hyprctl
            // keyword wants the keyword and value as separate args.
            var trimmed = line.TrimStart();
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var keyword = trimmed.Substring(0, eq).Trim();
            var value = trimmed.Substring(eq + 1).Trim();
            var (ok, _, _) = await RunAsync("hyprctl", new[] { "keyword", keyword, value }, ct).ConfigureAwait(false);
            if (!ok) anyFailed = true;
        }
        return !anyFailed;
    }

    private static string ResolveConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
        return Path.Combine(configHome, "hypr", "hyprland.conf");
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
