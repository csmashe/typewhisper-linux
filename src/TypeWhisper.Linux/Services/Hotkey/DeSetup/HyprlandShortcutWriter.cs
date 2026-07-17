using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Hyprland shortcut writer. On Write, a managed sentinel block with the
///     <c>bind</c>/<c>bindr</c>/cancel lines is upserted into
///     <c>~/.config/hypr/hyprland.conf</c>, then each line is applied live via
///     <c>hyprctl keyword</c>. If hyprctl fails the config write still succeeds
///     — a warning is surfaced instead of an error.
/// </summary>
public sealed class HyprlandShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "hyprland";
    public string DisplayName => "Hyprland";
    public bool SupportsPushToTalk => true;

    // hyprctl applies the bind live (a warning is surfaced if it couldn't).
    public bool RequiresSessionRestartToApply => false;

    public bool IsCurrentDesktop()
    {
        // HYPRLAND_INSTANCE_SIGNATURE is only set inside a live session;
        // hyprctl must also be present for the runtime-bind step.
        return DesktopDetector.DetectId() == "hyprland" && DesktopDetector.BinaryExists("hyprctl");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("~/.config/hypr/hyprland.conf — managed block:\n");
        foreach (var line in BuildManagedLines(spec))
        {
            sb.Append("  ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    public async Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var inner = SentinelBlock.ExtractBlockLines(existing);
            if (inner is null)
            {
                return false;
            }

            // Stale or manually edited blocks read as not-installed so the
            // checklist re-registers them.
            var expected = BuildManagedLines(spec).Select(l => l.TrimEnd()).ToList();
            return inner.SequenceEqual(expected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        var dir = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not create {dir}: {ex.Message}",
                []
            );
        }

        var existing = File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
            : string.Empty;
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
        {
            return new DeShortcutWriteResult(
                false,
                $"Your hyprland.conf has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually (remove the stray sentinel lines) and try again.",
                []
            );
        }

        var managed = BuildManagedLines(spec).ToList();
        var updated = SentinelBlock.ReplaceOrAppend(existing, managed);
        try
        {
            await AtomicFileWriter.WriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not write {path}: {ex.Message}",
                []
            );
        }

        // Apply live via hyprctl one line at a time to isolate failures.
        // Non-fatal — the persistent config is already written.
        var liveOk = await ApplyLiveAsync(spec, ct).ConfigureAwait(false);

        const string message = "Hyprland shortcut installed in ~/.config/hypr/hyprland.conf";
        var warning = liveOk
            ? null
            : "Config written, but `hyprctl` could not apply the bind live. Run `hyprctl reload` (or restart Hyprland) to pick it up.";
        return new DeShortcutWriteResult(true, message, [path], warning);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            return new DeShortcutWriteResult(
                true,
                "No hyprland.conf to update.",
                []
            );
        }

        var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
        {
            return new DeShortcutWriteResult(
                false,
                $"Your hyprland.conf has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
                []
            );
        }

        if (scan.OpenLine is null)
        {
            return new DeShortcutWriteResult(
                true,
                "No Hyprland integration to remove.",
                []
            );
        }

        var updated = SentinelBlock.Remove(existing);
        try
        {
            await AtomicFileWriter.WriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not write {path}: {ex.Message}",
                []
            );
        }

        // Hyprland's unbind syntax varies across versions; asking the user
        // to reload is more robust than attempting a live removal.
        return new DeShortcutWriteResult(
            true,
            "Hyprland managed block removed. Run `hyprctl reload` (or restart Hyprland) to drop the live binding.",
            [path]
        );
    }

    /// <summary>
    ///     Converts "Ctrl+Shift+Space" into Hyprland's ("CTRL SHIFT", "SPACE")
    ///     form. Modifiers are space-separated; key is uppercased for readability.
    /// </summary>
    public static (string mods, string key) ToHyprlandBind(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return (string.Empty, string.Empty);
        }

        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var mods = new List<string>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            mods.Add(
                p switch
                {
                    "ctrl" or "control" => "CTRL",
                    "shift" => "SHIFT",
                    "alt" => "ALT",
                    "super" or "win" or "windows" or "cmd" or "meta" => "SUPER",
                    _ => parts[i].ToUpperInvariant()
                }
            );
        }

        var key = parts[^1].ToUpperInvariant();
        return (string.Join(' ', mods), key);
    }

    private static IEnumerable<string> BuildManagedLines(DeShortcutSpec spec)
    {
        var (mods, key) = ToHyprlandBind(spec.Trigger);
        yield return $"bind  = {mods}, {key}, exec, {spec.OnPressCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnReleaseCommand))
        {
            yield return $"bindr = {mods}, {key}, exec, {spec.OnReleaseCommand}";
        }

        if (
            string.IsNullOrWhiteSpace(spec.OnCancelTrigger)
            || string.IsNullOrWhiteSpace(spec.OnCancelCommand)
        )
        {
            yield break;
        }

        var (cmods, ckey) = ToHyprlandBind(spec.OnCancelTrigger!);
        yield return $"bind  = {cmods}, {ckey}, exec, {spec.OnCancelCommand}";
    }

    private static async Task<bool> ApplyLiveAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("hyprctl"))
        {
            return false;
        }

        var anyFailed = false;
        foreach (var line in BuildManagedLines(spec))
        {
            // hyprctl keyword wants keyword and value as separate args.
            var trimmed = line.TrimStart();
            var eq = trimmed.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var keyword = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            var (ok, _, _) = await RunAsync("hyprctl", ["keyword", keyword, value], ct)
                .ConfigureAwait(false);
            if (!ok)
            {
                anyFailed = true;
            }
        }

        return !anyFailed;
    }

    private static string ResolveConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg) ? Path.Join(home, ".config") : xdg;
        return Path.Join(configHome, "hypr", "hyprland.conf");
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, string.Empty, $"Could not start {fileName}");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate to callers; only genuine process/apply
            // errors are flattened into the failure tuple below.
            throw;
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}