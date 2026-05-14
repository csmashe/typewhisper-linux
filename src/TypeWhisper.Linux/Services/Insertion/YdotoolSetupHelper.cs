using System.Diagnostics;
using System.IO;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
/// One-click installer for the ydotool stack on GNOME / KDE Wayland (and
/// any other compositor that the user has chosen to drive through
/// ydotool). The shape mirrors <see cref="IDeShortcutWriter"/> — the
/// Settings panel calls <see cref="IsCurrentlyConfigured"/> to paint the
/// status row, <see cref="PreviewLines"/> to show what would change, and
/// <see cref="SetUpAsync"/> to actually install. We don't implement that
/// interface because the surface differs (no per-DE plurality, no
/// shortcut spec), but the contract is intentionally familiar.
///
/// Install flow:
///   1. Write <c>/etc/udev/rules.d/60-ydotool.rules</c> via <c>pkexec</c>.
///   2. <c>systemctl --user enable --now ydotoold.service</c>.
///   3. Poll for the socket up to ~3 s before declaring success.
///
/// The udev rule grants the user's primary login session read/write
/// access to <c>/dev/uinput</c>; without it ydotoold runs but every
/// invocation fails with EACCES. <c>pkexec</c> is the consent surface —
/// we never call <c>sudo</c> directly because the GUI can't capture
/// terminal-style password prompts.
/// </summary>
public sealed class YdotoolSetupHelper
{
    private const string UdevRulePath = "/etc/udev/rules.d/60-ydotool.rules";
    // Marker used by RemoveAsync to confirm we own the file before
    // deleting it — without this we could nuke a rule a user or distro
    // package installed under the same conventional filename.
    internal const string OwnershipMarker = "Installed by TypeWhisper";
    private const string UdevRuleContent =
        "# " + OwnershipMarker + " — grants the active local session access\n" +
        "# to /dev/uinput so ydotoold can synthesize keystrokes for\n" +
        "# direct-typing fallback. TAG+=\"uaccess\" is the modern\n" +
        "# systemd-logind primitive: it grants the user on the currently\n" +
        "# active seat read/write without group membership or logout.\n" +
        "# The GROUP=\"input\" fallback covers init systems without\n" +
        "# logind (Devuan, Alpine without elogind, etc.).\n" +
        "KERNEL==\"uinput\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\", OPTIONS+=\"static_node=uinput\"\n";

    private readonly SystemCommandAvailabilityService _commands;

    public YdotoolSetupHelper(SystemCommandAvailabilityService commands)
    {
        _commands = commands;
    }

    public sealed record Status(
        bool BinaryInstalled,
        bool UdevRulePresent,
        bool SystemdUnitActive,
        bool SocketReachable,
        bool ProbeSucceeded,
        string? SocketPath)
    {
        /// <summary>
        /// True only when every layer is wired AND the daemon can actually
        /// write to /dev/uinput. <see cref="ProbeSucceeded"/> guards against
        /// the "socket exists but every keystroke fails EACCES" failure
        /// mode that happens on older systems where <c>TAG+="uaccess"</c>
        /// didn't apply and the user isn't in the input group. Without
        /// the probe field this property would lie and the status panel
        /// would say "Ready" alongside a setup-failed message.
        /// </summary>
        public bool IsFullyConfigured =>
            BinaryInstalled
            && UdevRulePresent
            && SystemdUnitActive
            && SocketReachable
            && ProbeSucceeded;
    }

    public sealed record SetupResult(bool Success, string Message, string? Detail = null);

    /// <summary>
    /// Cheap, side-effect-free probe of every component the install
    /// touches. Called on panel load and again after any
    /// <see cref="SetUpAsync"/> / <see cref="RemoveAsync"/> run. Includes
    /// a no-op functional probe (releases an unpressed key) so the
    /// returned status reflects whether ydotool can actually deliver
    /// keystrokes, not just whether the daemon's socket file exists.
    /// </summary>
    public Status IsCurrentlyConfigured()
    {
        var binary = DesktopDetector.BinaryExists(YdotoolBackend.ExecutableName);
        var rule = File.Exists(UdevRulePath);
        var unitActive = IsUserUnitActive("ydotoold.service");
        var socket = SystemCommandAvailabilityService.ResolveYdotoolSocketPath();
        // Only probe when the socket is reachable — without it the probe
        // would fail anyway and we'd burn a subprocess on a known-bad
        // state.
        var probed = socket is not null && RunSyncProbe(socket);
        return new Status(binary, rule, unitActive, socket is not null, probed, socket);
    }

    /// <summary>
    /// Synchronous probe used by <see cref="IsCurrentlyConfigured"/>.
    /// One-shot subprocess (~5 ms), tight 500 ms ceiling so a hung
    /// daemon can't wedge the status panel.
    /// </summary>
    private static bool RunSyncProbe(string socketPath)
    {
        try
        {
            var psi = new ProcessStartInfo(YdotoolBackend.ExecutableName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["YDOTOOL_SOCKET"] = socketPath },
            };
            foreach (var arg in YdotoolBackend.ProbeArgs())
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Human-readable preview of what <see cref="SetUpAsync"/> would
    /// execute. Pure: no disk touch, no process invocation. The user
    /// sees this in the panel before they click the button.
    /// </summary>
    public string PreviewLines()
    {
        return
            $"Write {UdevRulePath} via pkexec (one-time, requires admin password)\n" +
            "  KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\", OPTIONS+=\"static_node=uinput\"\n" +
            "systemctl --user enable --now ydotoold.service\n" +
            "Verify $XDG_RUNTIME_DIR/.ydotool_socket appears";
    }

    public async Task<SetupResult> SetUpAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists(YdotoolBackend.ExecutableName))
        {
            return new SetupResult(false,
                "ydotool is not installed. Use your package manager to install ydotool (and ydotoold).",
                Detail: "On Fedora: sudo dnf install ydotool. On Debian/Ubuntu: sudo apt install ydotool.");
        }

        // pkexec presence is only required when we actually need to write
        // the udev rule. If the rule is already on disk (manual install,
        // earlier setup run, distro default), we should be able to
        // finish setup with just the user-level systemctl invocation.
        if (!File.Exists(UdevRulePath))
        {
            var ruleInstalled = await InstallUdevRuleAsync(ct).ConfigureAwait(false);
            if (!ruleInstalled.Success)
                return ruleInstalled;
        }

        if (DesktopDetector.BinaryExists("systemctl"))
        {
            var unitOk = await EnableUserUnitAsync("ydotoold.service", ct).ConfigureAwait(false);
            if (!unitOk.Success)
                return unitOk;
        }
        else
        {
            return new SetupResult(false,
                "systemctl is not available on this system.",
                Detail: "Start ydotoold manually (it will not survive logout): nohup ydotoold &");
        }

        var socket = await WaitForSocketAsync(ct).ConfigureAwait(false);
        if (socket is null)
        {
            return new SetupResult(false,
                "ydotoold is running but the socket never appeared.",
                Detail: "Check `journalctl --user -u ydotoold` for daemon errors.");
        }

        // Functional probe — without this, "ready" can mean "socket is
        // up but every keystroke fails EACCES on /dev/uinput" because
        // the user isn't in the input group and TAG+=uaccess didn't
        // apply (older or non-logind systems). Refresh the snapshot
        // first so the live backend chain sees the new socket; even if
        // the probe fails the chain pick-up is still useful because the
        // user might fix permissions out-of-band.
        _commands.RefreshSnapshot();

        var probe = await ProbeYdotoolAsync(socket, ct).ConfigureAwait(false);
        if (!probe.Success)
            return probe;

        return new SetupResult(true, $"ydotool is ready. Socket: {socket}");
    }

    /// <summary>
    /// Run a no-op ydotool invocation to confirm the daemon can
    /// actually write to /dev/uinput. Distinguishes "permission denied"
    /// from other failures so the message can point at the right fix.
    /// </summary>
    private async Task<SetupResult> ProbeYdotoolAsync(string socketPath, CancellationToken ct)
    {
        var (ok, _, stderr) = await RunWithEnvAsync(
            YdotoolBackend.ExecutableName,
            YdotoolBackend.ProbeArgs(),
            new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath },
            ct).ConfigureAwait(false);

        if (ok)
            return new SetupResult(true, "ydotool probe succeeded.");

        if (LooksLikePermissionError(stderr))
        {
            return new SetupResult(false,
                "ydotoold can't write to /dev/uinput (permission denied).",
                Detail:
                    "On older systems where TAG+=\"uaccess\" doesn't apply, add yourself to the input group and log out / back in:\n" +
                    "  sudo usermod -aG input $USER\n" +
                    "Then re-open Settings → Text insertion to verify.");
        }

        return new SetupResult(false,
            "ydotool probe failed.",
            Detail: string.IsNullOrWhiteSpace(stderr) ? "Check `journalctl --user -u ydotoold`." : stderr.Trim());
    }

    private static bool LooksLikePermissionError(string stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not permitted", StringComparison.OrdinalIgnoreCase));

    public async Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        // Disable the user unit first so the socket goes away before we
        // pull the udev rule out from under it.
        if (DesktopDetector.BinaryExists("systemctl"))
            await RunAsync("systemctl", new[] { "--user", "disable", "--now", "ydotoold.service" }, ct).ConfigureAwait(false);

        var ruleNotOursMessage = (string?)null;
        if (File.Exists(UdevRulePath))
        {
            // Ownership guard: 60-ydotool.rules is the conventional name
            // used in every ydotool guide on the internet, so the user
            // (or a distro package) may have written one before
            // discovering TypeWhisper. Don't pkexec-rm privileged
            // config we didn't put there.
            if (!IsRuleOwnedByTypeWhisper(UdevRulePath))
            {
                ruleNotOursMessage =
                    $"Left {UdevRulePath} in place — it doesn't carry TypeWhisper's ownership marker, so we won't delete it. Remove it manually if you want to.";
            }
            else if (DesktopDetector.BinaryExists("pkexec"))
            {
                var (ok, _, err) = await RunAsync("pkexec",
                    new[] { "rm", "-f", UdevRulePath },
                    ct).ConfigureAwait(false);
                if (!ok)
                    return new SetupResult(false, $"Could not remove udev rule: {err.Trim()}");
            }
        }

        _commands.RefreshSnapshot();
        return new SetupResult(true,
            "ydotool integration removed.",
            Detail: ruleNotOursMessage);
    }

    private static bool IsRuleOwnedByTypeWhisper(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains(OwnershipMarker, StringComparison.Ordinal);
        }
        catch
        {
            // If we can't read the file, default to leaving it in place —
            // refusing is always safer than erasing privileged config we
            // can't even inspect.
            return false;
        }
    }

    private async Task<SetupResult> InstallUdevRuleAsync(CancellationToken ct)
    {
        // pkexec is only needed on the udev-rule write path; check it
        // here (the actual point of use) rather than at the top of
        // SetUpAsync — otherwise users whose rule is already installed
        // can't complete setup even though pkexec isn't needed.
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupResult(false,
                "pkexec is not available, so the udev rule can't be installed automatically.",
                Detail:
                    "Run this manually:\n" +
                    $"  sudo tee {UdevRulePath} > /dev/null <<'EOF'\n" +
                    UdevRuleContent +
                    "EOF\n" +
                    "  sudo udevadm control --reload && sudo udevadm trigger\n" +
                    "  systemctl --user enable --now ydotoold.service");
        }

        // pkexec runs the helper as root with the user's authentication.
        // We pipe the rule content through `tee` rather than passing it
        // on the command line so a content typo can't be persisted as
        // shell metadata.
        var script =
            $"set -e\n" +
            $"cat > {UdevRulePath} <<'EOF'\n" +
            UdevRuleContent +
            "EOF\n" +
            "udevadm control --reload\n" +
            "udevadm trigger --subsystem-match=misc --action=change || true\n";

        var (ok, stdout, stderr) = await RunWithStdinAsync(
            "pkexec",
            new[] { "/bin/sh" },
            script,
            ct).ConfigureAwait(false);

        if (!ok)
            return new SetupResult(false,
                "Could not install udev rule (pkexec failed or was canceled).",
                Detail: string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

        return new SetupResult(true, "Installed udev rule.");
    }

    private async Task<SetupResult> EnableUserUnitAsync(string unit, CancellationToken ct)
    {
        var (ok, _, err) = await RunAsync(
            "systemctl",
            new[] { "--user", "enable", "--now", unit },
            ct).ConfigureAwait(false);
        if (!ok)
        {
            return new SetupResult(false,
                $"Could not enable {unit}: {err.Trim()}",
                Detail:
                    "If your distro doesn't run user-instance systemd, start the daemon manually:\n" +
                    "  nohup ydotoold &\n" +
                    "(Note: this will not survive logout.)");
        }
        return new SetupResult(true, $"Started {unit}.");
    }

    private static async Task<string?> WaitForSocketAsync(CancellationToken ct)
    {
        // The systemd unit returns "started" before ydotoold has bound
        // its socket; poll briefly so the snapshot refresh below sees
        // the file when we exit.
        for (var attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
        {
            var path = SystemCommandAvailabilityService.ResolveYdotoolSocketPath();
            if (path is not null)
                return path;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsUserUnitActive(string unit)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl")
            {
                ArgumentList = { "--user", "is-active", "--quiet", unit },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Task<(bool ok, string stdout, string stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct) =>
        RunWithEnvAsync(fileName, args, env: null, ct);

    private static async Task<(bool ok, string stdout, string stderr)> RunWithEnvAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, string.Empty, $"Could not start {fileName}");
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode == 0, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunWithStdinAsync(
        string fileName,
        IReadOnlyList<string> args,
        string input,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, string.Empty, $"Could not start {fileName}");
            await p.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
            p.StandardInput.Close();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode == 0, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
