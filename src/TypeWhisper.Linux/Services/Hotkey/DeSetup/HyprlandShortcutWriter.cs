using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Hyprland shortcut writer. On Write, a managed sentinel block with the
///     <c>bind</c>/<c>bindr</c>/cancel lines is upserted into
///     <c>~/.config/hypr/hyprland.conf</c>, then the compositor is reloaded so
///     replaced or removed binds are dropped as well. If <c>hyprctl reload</c> fails,
///     the config write still succeeds and a warning is surfaced instead of an error.
/// </summary>
public sealed class HyprlandShortcutWriter : IDeShortcutWriter
{
    private const int MaxWriteAttempts = 3;
    private const string RemovalRequiresReloadWarning =
        "Hyprland may still have the live binding. Run `hyprctl reload` (or restart Hyprland) to remove it.";

    private readonly Func<
        AtomicFileSnapshot,
        string,
        CancellationToken,
        Task<bool>
    > _conditionalWriteAsync;

    public HyprlandShortcutWriter()
        : this(AtomicFileWriter.WriteIfUnchangedAsync) { }

    internal HyprlandShortcutWriter(
        Func<AtomicFileSnapshot, string, CancellationToken, Task<bool>> conditionalWriteAsync
    )
    {
        _conditionalWriteAsync = conditionalWriteAsync
                                 ?? throw new ArgumentNullException(nameof(conditionalWriteAsync));
    }

    public string DesktopId => "hyprland";
    public string DisplayName => "Hyprland";
    public bool SupportsPushToTalk => true;

    // hyprctl reload applies the committed config live (a warning is surfaced if it couldn't).
    public bool RequiresSessionRestartToApply => false;

    public bool IsCurrentDesktop()
    {
        // HYPRLAND_INSTANCE_SIGNATURE is only set inside a live session;
        // hyprctl must also be present for the live reload step.
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
        var inner = await ReadManagedBlockLinesAsync(ct).ConfigureAwait(false);
        // Stale or manually edited blocks read as not-installed so the
        // checklist re-registers them.
        var expected = BuildManagedLines(spec).Select(l => l.TrimEnd()).ToList();
        return inner is not null && inner.SequenceEqual(expected);
    }

    public async Task<bool> IsManagedShortcutPresentAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        return await ReadManagedBlockLinesAsync(ct).ConfigureAwait(false) is not null;
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

        var managed = BuildManagedLines(spec).ToList();
        var committed = false;
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            try
            {
                var snapshot = await AtomicFileWriter.CaptureAsync(path, ct)
                    .ConfigureAwait(false);
                var scan = SentinelBlock.Scan(snapshot.Contents);
                if (scan.Mismatched)
                {
                    return new DeShortcutWriteResult(
                        false,
                        $"Your hyprland.conf has an unbalanced TypeWhisper managed block. {scan.Reason} Fix it manually (remove the stray sentinel lines) and try again.",
                        []
                    );
                }

                var updated = SentinelBlock.ReplaceOrAppend(snapshot.Contents, managed);
                // ReSharper disable once InvertIf -- the conditional-write commit/break is the deliberate success path of the capture-and-retry loop; leave it as-is rather than inverting into a continue.
                if (
                    await _conditionalWriteAsync(snapshot, updated, ct).ConfigureAwait(false)
                )
                {
                    committed = true;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DeShortcutWriteResult(
                    false,
                    $"Could not write {path}: {ex.Message}",
                    []
                );
            }
        }

        if (!committed)
        {
            return new DeShortcutWriteResult(
                false,
                "hyprland.conf kept changing while TypeWhisper was updating it. Please retry.",
                []
            );
        }

        // A full reload is required to drop any old trigger/release/cancel binds that
        // were replaced in the persistent block. Non-fatal: the config is committed.
        var liveOk = await ReloadAsync(ct).ConfigureAwait(false);

        const string message = "Hyprland shortcut installed in ~/.config/hypr/hyprland.conf";
        var warning = liveOk
            ? null
            : "Config written, but `hyprctl reload` failed. Reload or restart Hyprland to pick up the binding.";
        return new DeShortcutWriteResult(true, message, [path], warning);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = ResolveConfigPath();
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            try
            {
                var snapshot = await AtomicFileWriter.CaptureAsync(path, ct)
                    .ConfigureAwait(false);
                if (!snapshot.Existed)
                {
                    return new DeShortcutWriteResult(
                        true,
                        "No hyprland.conf to update.",
                        [],
                        RemovalRequiresReloadWarning
                    );
                }

                var scan = SentinelBlock.Scan(snapshot.Contents);
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
                        [],
                        RemovalRequiresReloadWarning
                    );
                }

                var updated = SentinelBlock.Remove(snapshot.Contents);
                if (
                    !await _conditionalWriteAsync(snapshot, updated, ct).ConfigureAwait(false)
                )
                {
                    continue;
                }

                var reloaded = await ReloadAsync(ct).ConfigureAwait(false);
                var warning = reloaded
                    ? null
                    : RemovalRequiresReloadWarning;
                return new DeShortcutWriteResult(
                    true,
                    "Hyprland managed block removed.",
                    [path],
                    warning
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DeShortcutWriteResult(
                    false,
                    $"Could not write {path}: {ex.Message}",
                    []
                );
            }
        }

        return new DeShortcutWriteResult(
            false,
            "hyprland.conf kept changing while TypeWhisper was removing its managed block. Please retry.",
            []
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

    private static async Task<IReadOnlyList<string>?> ReadManagedBlockLinesAsync(
        CancellationToken ct
    )
    {
        var path = ResolveConfigPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var scan = SentinelBlock.Scan(existing);
            if (scan.Mismatched || scan.OpenLine is null)
            {
                return null;
            }

            return SentinelBlock.ExtractBlockLines(existing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> ReloadAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("hyprctl"))
        {
            return false;
        }

        var (ok, _, _) = await RunAsync("hyprctl", ["reload"], ct).ConfigureAwait(false);
        return ok;
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
