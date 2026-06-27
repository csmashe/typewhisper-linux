using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Sway shortcut writer. Config syntax differs from Hyprland and live-apply
///     uses <c>swaymsg reload</c> (Sway lacks per-bind IPC, so a full reload is
///     the only reliable path).
/// </summary>
public sealed class SwayShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "sway";
    public string DisplayName => "Sway";
    public bool SupportsPushToTalk => true;

    // swaymsg reload applies the bind live (a warning is surfaced if it couldn't).
    public bool RequiresSessionRestartToApply => false;

    public bool IsCurrentDesktop()
    {
        return DesktopDetector.DetectId() == "sway" && DesktopDetector.BinaryExists("swaymsg");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("~/.config/sway/config — managed block:\n");
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

            // Must match exactly — a stale trigger or manual edit reads as not-installed.
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
                $"Your sway config has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
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

        var reloaded = await ReloadAsync(ct).ConfigureAwait(false);
        const string message = "Sway shortcut installed in ~/.config/sway/config";
        var warning = reloaded
            ? null
            : "Config written, but `swaymsg reload` failed. Reload Sway manually to pick up the binding.";
        return new DeShortcutWriteResult(true, message, [path], warning);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            return new DeShortcutWriteResult(
                true,
                "No sway config to update.",
                []
            );
        }

        var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var scan = SentinelBlock.Scan(existing);
        if (scan.Mismatched)
        {
            return new DeShortcutWriteResult(
                false,
                $"Your sway config has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually and try again.",
                []
            );
        }

        if (scan.OpenLine is null)
        {
            return new DeShortcutWriteResult(
                true,
                "No Sway integration to remove.",
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

        var reloaded = await ReloadAsync(ct).ConfigureAwait(false);
        var warning = reloaded
            ? null
            : "Block removed, but `swaymsg reload` failed. Reload Sway manually to drop the live bindings.";
        return new DeShortcutWriteResult(
            true,
            "Sway managed block removed.",
            [path],
            warning
        );
    }

    /// <summary>
    ///     Converts "Ctrl+Shift+Space" to Sway's "Ctrl+Shift+space" form.
    ///     Sway key names are lower-case (xkbcommon convention); "+" separates tokens.
    /// </summary>
    public static string ToSwayBind(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return string.Empty;
        }

        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return string.Empty;
        }

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
                _ => parts[i]
            };
            if (sb.Length > 0)
            {
                sb.Append('+');
            }

            sb.Append(mapped);
        }

        var tail = parts[^1];
        if (sb.Length > 0)
        {
            sb.Append('+');
        }

        // Multi-char keys are lower-cased (xkbcommon convention) except
        // function keys, whose keysyms are mixed-case ("F1".."F35").
        sb.Append(NormalizeSwayKey(tail));
        return sb.ToString();
    }

    private static IEnumerable<string> BuildManagedLines(DeShortcutSpec spec)
    {
        var trigger = ToSwayBind(spec.Trigger);
        // --no-repeat suppresses key-repeat spam during PTT; Sway still delivers press + release.
        yield return $"bindsym --no-repeat {trigger} exec {spec.OnPressCommand}";
        if (!string.IsNullOrWhiteSpace(spec.OnReleaseCommand))
        {
            yield return $"bindsym --release {trigger} exec {spec.OnReleaseCommand}";
        }

        if (
            !string.IsNullOrWhiteSpace(spec.OnCancelTrigger)
            && !string.IsNullOrWhiteSpace(spec.OnCancelCommand)
        )
        {
            yield return $"bindsym {ToSwayBind(spec.OnCancelTrigger!)} exec {spec.OnCancelCommand}";
        }
    }

    private static string NormalizeSwayKey(string key)
    {
        if (key.Length <= 1)
        {
            return key;
        }

        if (IsFunctionKey(key))
        {
            return "F" + key[1..];
        }

        return key.ToLowerInvariant();
    }

    private static bool IsFunctionKey(string k)
    {
        if (k.Length < 2 || (k[0] != 'F' && k[0] != 'f'))
        {
            return false;
        }

        for (var i = 1; i < k.Length; i++)
        {
            if (!char.IsDigit(k[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg) ? Path.Join(home, ".config") : xdg;
        return Path.Join(configHome, "sway", "config");
    }

    private static async Task<bool> ReloadAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("swaymsg"))
        {
            return false;
        }

        var (ok, _, _) = await RunAsync("swaymsg", ["reload"], ct).ConfigureAwait(false);
        return ok;
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