using System.IO;
using System.Runtime.InteropServices;
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

    // The user-level systemd unit name. Fedora's ydotool package ships
    // only a system-level `ydotool.service` (runs ydotoold as root, with
    // a root-owned socket the user can't reach), so on a clean install no
    // user unit by this name resolves and we write our own.
    internal const string UserUnitName = "ydotoold.service";

    /// <summary>
    /// Absolute path to the user-level systemd unit we install when the
    /// distro doesn't ship one. Honors <c>XDG_CONFIG_HOME</c>, falling
    /// back to <c>~/.config</c>. Pure — no disk touch.
    /// </summary>
    internal static string UserUnitFilePath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "systemd", "user", UserUnitName);
    }

    /// <summary>
    /// Builds the user-level <c>ydotoold.service</c> unit text. The first
    /// line carries <see cref="OwnershipMarker"/> so <see cref="RemoveAsync"/>
    /// can confirm we own the file before deleting it. Pure.
    /// </summary>
    internal static string BuildUserUnitContent(string ydotooldPath)
    {
        return
            "# " + OwnershipMarker + " — user-level ydotoold service so direct-typing\n" +
            "# works without a system unit. Delete this file to roll back.\n" +
            "[Unit]\n" +
            "Description=ydotool daemon (user) — installed by TypeWhisper\n" +
            "Documentation=https://github.com/ReimuNotMoe/ydotool\n" +
            "After=default.target\n" +
            "\n" +
            "[Service]\n" +
            "Type=simple\n" +
            $"ExecStart={ydotooldPath}\n" +
            "Restart=on-failure\n" +
            "RestartSec=2\n" +
            "\n" +
            "[Install]\n" +
            "WantedBy=default.target\n";
    }

    /// <summary>
    /// Walks <c>$PATH</c> and returns the absolute path of the named
    /// binary, or <c>null</c> if it isn't reachable. Mirrors
    /// <see cref="DesktopDetector.BinaryExists"/> but returns the path —
    /// kept local to this helper since no other caller needs it.
    /// </summary>
    internal static string? ResolveBinaryPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Bad PATH entry — skip.
            }
        }
        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string pathname, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <summary>
    /// True only when this process can already read+write <c>/dev/uinput</c>
    /// (R_OK|W_OK = 6) — the ground-truth signal that the udev rule is
    /// unnecessary. Running as root is treated as "not accessible": root
    /// can always write the node, but the real non-root user still needs
    /// the rule, so we don't let a root-run GUI skip installing it.
    /// </summary>
    private static bool UinputIsAccessible()
    {
        try
        {
            if (geteuid() == 0) return false;
            return File.Exists("/dev/uinput") && access("/dev/uinput", 6) == 0;
        }
        catch
        {
            return false;
        }
    }

    private readonly SystemCommandAvailabilityService _commands;
    private readonly IProcessRunner _runner;

    public YdotoolSetupHelper(SystemCommandAvailabilityService commands, IProcessRunner runner)
    {
        _commands = commands;
        _runner = runner;
    }

    public sealed record Status(
        bool BinaryInstalled,
        bool UdevRulePresent,
        bool SystemdUnitActive,
        bool SocketReachable,
        bool ProbeSucceeded,
        bool UinputAccessible,
        string? SocketPath)
    {
        /// <summary>
        /// True only when every layer is wired AND the daemon can actually
        /// write to /dev/uinput. <see cref="ProbeSucceeded"/> guards against
        /// the "socket exists but every keystroke fails EACCES" failure
        /// mode that happens on older systems where <c>TAG+="uaccess"</c>
        /// didn't apply and the user isn't in the input group. The udev
        /// rule is satisfied either by our installed rule
        /// (<see cref="UdevRulePresent"/>) or by the kernel already
        /// granting access (<see cref="UinputAccessible"/>) — setup skips
        /// installing the rule in the latter case, so it can't be a hard
        /// requirement here.
        /// </summary>
        public bool IsFullyConfigured =>
            BinaryInstalled
            && SystemdUnitActive
            && SocketReachable
            && ProbeSucceeded
            && (UdevRulePresent || UinputAccessible);
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
        var unitActive = IsUserUnitActive(UserUnitName);
        var socket = SystemCommandAvailabilityService.ResolveYdotoolSocketPath();
        // Only probe when the socket is reachable — without it the probe
        // would fail anyway and we'd burn a subprocess on a known-bad
        // state.
        var probed = socket is not null && RunSyncProbe(socket);
        var uinputAccessible = UinputIsAccessible();
        return new Status(binary, rule, unitActive, socket is not null, probed, uinputAccessible, socket);
    }

    /// <summary>
    /// Synchronous probe used by <see cref="IsCurrentlyConfigured"/>.
    /// One-shot subprocess (~5 ms), tight 500 ms ceiling so a hung
    /// daemon can't wedge the status panel. Blocks the caller — same as
    /// before the <see cref="IProcessRunner"/> seam; the runner uses
    /// ConfigureAwait(false) throughout so there is no UI-thread deadlock.
    /// </summary>
    private bool RunSyncProbe(string socketPath)
    {
        var result = _runner.RunAsync(
            YdotoolBackend.ExecutableName,
            YdotoolBackend.ProbeArgs(),
            environment: new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath },
            timeout: TimeSpan.FromMilliseconds(500))
            .GetAwaiter().GetResult();
        return result.Succeeded;
    }

    /// <summary>
    /// Human-readable preview of what <see cref="SetUpAsync"/> would
    /// execute. Pure: no disk touch, no process invocation. The user
    /// sees this in the panel before they click the button.
    /// </summary>
    public string PreviewLines()
    {
        return
            $"If /dev/uinput isn't already accessible: install {UdevRulePath} via\n" +
            "  pkexec (one-time admin prompt).\n" +
            $"If no ydotoold user service exists: write {UserUnitFilePath()}\n" +
            "  and run `systemctl --user daemon-reload`.\n" +
            "systemctl --user enable --now ydotoold.service\n" +
            "Verify the ydotool socket appears";
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
        // the udev rule. Skip it entirely when the rule is already on disk
        // (manual install, earlier setup run, distro default) OR when the
        // kernel already grants this process read/write on /dev/uinput —
        // in that case the rule is genuinely unnecessary and prompting for
        // an admin password would just be a needless nag.
        if (!File.Exists(UdevRulePath) && !UinputIsAccessible())
        {
            var ruleInstalled = await InstallUdevRuleAsync(ct).ConfigureAwait(false);
            if (!ruleInstalled.Success)
                return ruleInstalled;
        }

        if (!DesktopDetector.BinaryExists("systemctl"))
        {
            return new SetupResult(false,
                "systemctl is not available on this system.",
                Detail: "Start ydotoold manually (it will not survive logout): nohup ydotoold &");
        }

        // Fedora's ydotool package ships only a system-level unit, so on a
        // clean install no user `ydotoold.service` resolves. Write our own
        // before trying to enable it.
        var (unitFile, wroteUnit) = await EnsureUserUnitExistsAsync(ct).ConfigureAwait(false);
        if (!unitFile.Success)
            return unitFile;
        if (wroteUnit)
        {
            var reload = await _runner.RunAsync(
                "systemctl", new[] { "--user", "daemon-reload" }, ct: ct).ConfigureAwait(false);
            if (!reload.Succeeded)
                return new SetupResult(false,
                    "Could not reload the systemd user manager.",
                    Detail: string.IsNullOrWhiteSpace(reload.StandardError)
                        ? "Check `systemctl --user status`."
                        : reload.StandardError.Trim());
        }
        var unitOk = await EnableUserUnitAsync(UserUnitName, ct).ConfigureAwait(false);
        if (!unitOk.Success)
            return unitOk;

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

        return new SetupResult(true,
            $"ydotool is ready. Socket: {socket}. It starts automatically on login.");
    }

    /// <summary>
    /// Run a no-op ydotool invocation to confirm the daemon can
    /// actually write to /dev/uinput. Distinguishes "permission denied"
    /// from other failures so the message can point at the right fix.
    /// </summary>
    private async Task<SetupResult> ProbeYdotoolAsync(string socketPath, CancellationToken ct)
    {
        var probe = await _runner.RunAsync(
            YdotoolBackend.ExecutableName,
            YdotoolBackend.ProbeArgs(),
            environment: new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath },
            ct: ct).ConfigureAwait(false);

        if (probe.Succeeded)
            return new SetupResult(true, "ydotool probe succeeded.");

        if (LooksLikePermissionError(probe.StandardError))
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
            Detail: string.IsNullOrWhiteSpace(probe.StandardError)
                ? "Check `journalctl --user -u ydotoold`."
                : probe.StandardError.Trim());
    }

    private static bool LooksLikePermissionError(string stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not permitted", StringComparison.OrdinalIgnoreCase));

    public async Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        // Ownership gate for *both* the disable and the file delete. We only
        // touch a user unit we wrote: SetUpAsync respects a pre-existing
        // foreign ydotoold user unit (distro/AUR/manual) and won't overwrite
        // its file — but it does `enable --now` whatever unit resolves. If we
        // then unconditionally `disable --now`d here, clicking Remove would
        // disable a service the user relies on, leaving it dead after the
        // next login while the foreign file stays in place. So: foreign unit
        // → leave its enablement state entirely alone.
        var unitPath = UserUnitFilePath();
        var weOwnUnit = File.Exists(unitPath) && IsFileOwnedByTypeWhisper(unitPath);

        // Disable our user unit first so the socket goes away before we pull
        // the udev rule out from under it. Fail closed: if `disable --now`
        // fails, ydotoold may still be running and — worse — the enablement
        // symlink may survive; deleting the unit file then would leave a
        // dangling symlink that makes every later `systemctl --user` call
        // warn. Abort before the delete and surface the error instead.
        if (weOwnUnit && DesktopDetector.BinaryExists("systemctl"))
        {
            var disable = await _runner.RunAsync(
                "systemctl", new[] { "--user", "disable", "--now", UserUnitName }, ct: ct).ConfigureAwait(false);
            if (!disable.Succeeded)
                return new SetupResult(false,
                    $"Could not disable {UserUnitName} — left {unitPath} in place so you can retry.",
                    Detail: string.IsNullOrWhiteSpace(disable.StandardError)
                        ? "Check `systemctl --user status ydotoold.service`."
                        : disable.StandardError.Trim());
        }

        // Delete our user unit file if we own it. Mirrors the udev-rule
        // ownership guard: a unit a distro package or the user wrote at
        // the same path stays in place. Teardown order: disable → delete
        // file → daemon-reload.
        var removedUnit = false;
        string? unitNotOursMessage = null;
        if (File.Exists(unitPath))
        {
            if (weOwnUnit)
            {
                try
                {
                    File.Delete(unitPath);
                    removedUnit = true;
                }
                catch (Exception ex)
                {
                    // Fail closed, mirroring the disable-failure path above:
                    // a TypeWhisper-owned unit still on disk means removal
                    // did not complete, so don't report success.
                    return new SetupResult(false,
                        $"Could not delete {unitPath}: {ex.Message}");
                }
            }
            else
            {
                unitNotOursMessage =
                    $"Left {unitPath} in place and untouched — it has no TypeWhisper ownership marker, so its ydotoold service stays enabled.";
            }
        }
        if (removedUnit && DesktopDetector.BinaryExists("systemctl"))
            await _runner.RunAsync("systemctl", new[] { "--user", "daemon-reload" }, ct: ct).ConfigureAwait(false);

        var ruleNotOursMessage = (string?)null;
        if (File.Exists(UdevRulePath))
        {
            // Ownership guard: 60-ydotool.rules is the conventional name
            // used in every ydotool guide on the internet, so the user
            // (or a distro package) may have written one before
            // discovering TypeWhisper. Don't pkexec-rm privileged
            // config we didn't put there.
            if (!IsFileOwnedByTypeWhisper(UdevRulePath))
            {
                ruleNotOursMessage =
                    $"Left {UdevRulePath} in place — it doesn't carry TypeWhisper's ownership marker, so we won't delete it. Remove it manually if you want to.";
            }
            else if (DesktopDetector.BinaryExists("pkexec"))
            {
                var rm = await _runner.RunAsync("pkexec",
                    new[] { "rm", "-f", UdevRulePath },
                    ct: ct).ConfigureAwait(false);
                if (!rm.Succeeded)
                    return new SetupResult(false, $"Could not remove udev rule: {rm.StandardError.Trim()}");
            }
        }

        _commands.RefreshSnapshot();

        var detail = string.Join(
            "\n",
            new[] { unitNotOursMessage, ruleNotOursMessage }
                .Where(m => !string.IsNullOrWhiteSpace(m)));
        return new SetupResult(true,
            "ydotool integration removed.",
            Detail: string.IsNullOrEmpty(detail) ? null : detail);
    }

    internal static bool IsFileOwnedByTypeWhisper(string path)
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

        var run = await _runner.RunAsync(
            "pkexec",
            new[] { "/bin/sh" },
            standardInput: script,
            ct: ct).ConfigureAwait(false);

        if (!run.Succeeded)
            return new SetupResult(false,
                "Could not install udev rule (pkexec failed or was canceled).",
                Detail: string.IsNullOrWhiteSpace(run.StandardError) ? run.StandardOutput : run.StandardError);

        return new SetupResult(true, "Installed udev rule.");
    }

    /// <summary>
    /// Ensures a user-level <c>ydotoold.service</c> resolves. If one
    /// already does (the user, or a distro/AUR package, set one up) we
    /// respect it and don't overwrite. Otherwise we atomically write our
    /// own to <see cref="UserUnitFilePath"/>. Returns whether the unit
    /// file was newly written so the caller can decide whether a
    /// <c>daemon-reload</c> is needed — keeping <see cref="SetUpAsync"/>
    /// free of mutable instance state (this helper is a DI singleton).
    /// </summary>
    private async Task<(SetupResult result, bool wroteUnitFile)> EnsureUserUnitExistsAsync(
        CancellationToken ct)
    {
        // `systemctl --user cat` exits 0 only when a unit by this name
        // resolves through the full unit search path — covers a unit the
        // user wrote, or one shipped by a distro/AUR package, wherever it
        // lives. Respect any such unit rather than shadowing it.
        var cat = await _runner.RunAsync(
            "systemctl", new[] { "--user", "cat", UserUnitName }, ct: ct).ConfigureAwait(false);
        if (cat.Succeeded)
            return (new SetupResult(true, "A ydotoold user service already exists."), false);

        // No user unit resolves — we must write our own, which needs the
        // daemon's absolute path for ExecStart. Resolve it only here, not up
        // front in SetUpAsync: an already-resolving unit (handled above) may
        // legitimately point ExecStart at a ydotoold outside this process's
        // $PATH, and rejecting that working setup just because we can't find
        // the binary ourselves would be wrong.
        var ydotooldPath = ResolveBinaryPath("ydotoold");
        if (ydotooldPath is null)
            return (new SetupResult(false,
                "ydotoold (the ydotool daemon) is not installed.",
                Detail: "On Fedora the `ydotool` package includes it: sudo dnf install ydotool"), false);

        var unitPath = UserUnitFilePath();
        try
        {
            // Ownership guard, mirroring the udev-rule check: if a file is
            // already at our path but lacks our marker, the user put it
            // there — don't clobber it.
            if (File.Exists(unitPath) && !IsFileOwnedByTypeWhisper(unitPath))
            {
                return (new SetupResult(false,
                    $"A ydotoold user unit already exists at {unitPath} but doesn't carry TypeWhisper's ownership marker.",
                    Detail: "Remove or fix that file manually, then run setup again."), false);
            }

            // Atomic write: temp file + move, same pattern as
            // BrowserAccessibilitySetupHelper.WriteEnvFile.
            Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
            var tempPath = unitPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, BuildUserUnitContent(ydotooldPath), ct).ConfigureAwait(false);
            File.Move(tempPath, unitPath, overwrite: true);
            return (new SetupResult(true, $"Wrote {unitPath}."), true);
        }
        catch (Exception ex)
        {
            return (new SetupResult(false,
                "Could not write the ydotoold user unit.",
                Detail: ex.Message), false);
        }
    }

    private async Task<SetupResult> EnableUserUnitAsync(string unit, CancellationToken ct)
    {
        var enable = await _runner.RunAsync(
            "systemctl",
            new[] { "--user", "enable", "--now", unit },
            ct: ct).ConfigureAwait(false);
        if (!enable.Succeeded)
        {
            return new SetupResult(false,
                $"Could not enable {unit}: {enable.StandardError.Trim()}",
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

    private bool IsUserUnitActive(string unit)
    {
        var result = _runner.RunAsync(
            "systemctl",
            new[] { "--user", "is-active", "--quiet", unit },
            timeout: TimeSpan.FromMilliseconds(500))
            .GetAwaiter().GetResult();
        return result.Succeeded;
    }
}
