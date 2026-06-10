using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>
///     One-click installer for the ydotool stack. Install flow:
///     1. Write <c>/etc/udev/rules.d/60-ydotool.rules</c> via <c>pkexec</c> (one-time admin prompt).
///     2. <c>systemctl --user enable --now ydotoold.service</c>.
///     3. Poll up to ~3 s for the socket before declaring success.
///     The udev rule grants the active session read/write access to <c>/dev/uinput</c>;
///     without it ydotoold starts but every keystroke fails with EACCES.
///     <c>pkexec</c> is the consent surface — we never call <c>sudo</c> directly.
/// </summary>
public sealed class YdotoolSetupHelper
{
    private const string UdevRulePath = "/etc/udev/rules.d/60-ydotool.rules";

    // Marker used by RemoveAsync to confirm we own the file before
    // deleting it — without this we could nuke a rule a user or distro
    // package installed under the same conventional filename.
    internal const string OwnershipMarker = "Installed by TypeWhisper";

    private const string UdevRuleContent =
        "# "
        + OwnershipMarker
        + " — grants the active local session access\n"
        + "# to /dev/uinput so ydotoold can synthesize keystrokes for\n"
        + "# direct-typing fallback. TAG+=\"uaccess\" is the modern\n"
        + "# systemd-logind primitive: it grants the user on the currently\n"
        + "# active seat read/write without group membership or logout.\n"
        + "# The GROUP=\"input\" fallback covers init systems without\n"
        + "# logind (Devuan, Alpine without elogind, etc.).\n"
        + "KERNEL==\"uinput\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\", OPTIONS+=\"static_node=uinput\"\n";

    // The udev rule above can only grant access to a device whose kernel
    // module is actually loaded. Distros like Arch / Omarchy do NOT auto-load
    // uinput, so /dev/uinput exists only as a root-owned static node and the
    // rule never applies — ydotoold then fails to open it (EACCES, exit 2) and
    // crash-loops into systemd's start-limit. We fix that by loading the module
    // now (modprobe, in the pkexec script) and persisting it across reboots via
    // modules-load.d so the device is present for udev to apply the rule to on
    // every boot.
    private const string ModulesLoadPath = "/etc/modules-load.d/uinput.conf";

    private const string ModulesLoadContent =
        "# "
        + OwnershipMarker
        + " — load the uinput kernel module at boot so /dev/uinput exists\n"
        + "# for the udev rule (60-ydotool.rules) to grant access. Without this,\n"
        + "# distros that don't auto-load uinput (e.g. Arch) leave ydotoold unable\n"
        + "# to open the device. Delete this file to roll back.\n"
        + "uinput\n";

    // The user-level systemd unit name. Fedora's ydotool package ships
    // only a system-level `ydotool.service` (runs ydotoold as root, with
    // a root-owned socket the user can't reach), so on a clean install no
    // user unit by this name resolves and we write our own.
    internal const string UserUnitName = "ydotoold.service";

    private readonly SystemCommandAvailabilityService _commands;
    private readonly IProcessRunner _runner;

    public YdotoolSetupHelper(SystemCommandAvailabilityService commands, IProcessRunner runner)
    {
        _commands = commands;
        _runner = runner;
    }

    /// <summary>
    ///     Cheap, side-effect-free probe of every component the install
    ///     touches. Called on panel load and again after any
    ///     <see cref="SetUpAsync" /> / <see cref="RemoveAsync" /> run. Includes
    ///     a no-op functional probe (releases an unpressed key) so the
    ///     returned status reflects whether ydotool can actually deliver
    ///     keystrokes, not just whether the daemon's socket file exists.
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
        return new Status(
            binary,
            rule,
            unitActive,
            socket is not null,
            probed,
            uinputAccessible,
            socket
        );
    }

    /// <summary>
    ///     Human-readable preview of what <see cref="SetUpAsync" /> would
    ///     execute. Pure: no disk touch, no process invocation. The user
    ///     sees this in the panel before they click the button.
    /// </summary>
    public string PreviewLines()
    {
        return $"If /dev/uinput isn't already accessible: install {UdevRulePath} via\n"
               + "  pkexec (one-time admin prompt).\n"
               + $"If no ydotoold user service exists: write {UserUnitFilePath()}\n"
               + "  and run `systemctl --user daemon-reload`.\n"
               + "systemctl --user enable --now ydotoold.service\n"
               + "Verify the ydotool socket appears";
    }

    public async Task<SetupResult> SetUpAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists(YdotoolBackend.ExecutableName))
        {
            return new SetupResult(
                false,
                "ydotool is not installed. Use your package manager to install ydotool (and ydotoold).",
                "On Fedora: sudo dnf install ydotool. On Debian/Ubuntu: sudo apt install ydotool."
            );
        }

        // Gate on "can we actually read+write /dev/uinput?" not "does the rule file exist?".
        // A prior run may have written the rule but left the module unloaded, so the device
        // is still inaccessible. Only skip the privileged path when access is confirmed.
        if (!UinputIsAccessible())
        {
            var ruleInstalled = await InstallUdevRuleAsync(ct).ConfigureAwait(false);
            if (!ruleInstalled.Success)
            {
                return ruleInstalled;
            }
        }

        if (!DesktopDetector.BinaryExists("systemctl"))
        {
            return new SetupResult(
                false,
                "systemctl is not available on this system.",
                "Start ydotoold manually (it will not survive logout): nohup ydotoold &"
            );
        }

        // Fedora's ydotool package ships only a system-level unit, so on a
        // clean install no user `ydotoold.service` resolves. Write our own
        // before trying to enable it.
        var (unitFile, wroteUnit) = await EnsureUserUnitExistsAsync(ct).ConfigureAwait(false);
        if (!unitFile.Success)
        {
            return unitFile;
        }

        if (wroteUnit)
        {
            var reload = await _runner
                .RunAsync("systemctl", new[] { "--user", "daemon-reload" }, ct: ct)
                .ConfigureAwait(false);
            if (!reload.Succeeded)
            {
                return new SetupResult(
                    false,
                    "Could not reload the systemd user manager.",
                    string.IsNullOrWhiteSpace(reload.StandardError)
                        ? "Check `systemctl --user status`."
                        : reload.StandardError.Trim()
                );
            }
        }

        var unitOk = await EnableUserUnitAsync(UserUnitName, ct).ConfigureAwait(false);
        if (!unitOk.Success)
        {
            return unitOk;
        }

        // Restart the daemon so it (re)opens /dev/uinput AFTER the udev rule's
        // uaccess ACL has been applied. `systemctl --user enable --now` does NOT
        // restart an already-running ydotoold, so on a first install — where the
        // daemon may have started moments before the ACL landed — it would keep a
        // stale EACCES handle and silently fail every keystroke while the socket
        // looks healthy. A restart guarantees a fresh open with current perms.
        await _runner
            .RunAsync("systemctl", new[] { "--user", "restart", UserUnitName }, ct: ct)
            .ConfigureAwait(false);

        var socket = await WaitForSocketAsync(ct).ConfigureAwait(false);
        if (socket is null)
        {
            return new SetupResult(
                false,
                "ydotoold is running but the socket never appeared.",
                "Check `journalctl --user -u ydotoold` for daemon errors."
            );
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
        {
            return probe;
        }

        return new SetupResult(
            true,
            $"ydotool is ready. Socket: {socket}. It starts automatically on login."
        );
    }

    public async Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        // Only touch units we wrote. SetUpAsync enables any resolving unit (including foreign
        // distro/AUR units), so unconditionally disabling here would kill a service the user
        // relies on. Foreign unit → leave its enablement state entirely alone.
        var unitPath = UserUnitFilePath();
        var weOwnUnit = File.Exists(unitPath) && IsFileOwnedByTypeWhisper(unitPath);

        // Disable first so the socket goes away before the udev rule is removed.
        // Fail closed: if disable fails, the enablement symlink may survive and a
        // subsequent file delete would leave it dangling — abort and surface the error.
        if (weOwnUnit && DesktopDetector.BinaryExists("systemctl"))
        {
            var disable = await _runner
                .RunAsync("systemctl", new[] { "--user", "disable", "--now", UserUnitName }, ct: ct)
                .ConfigureAwait(false);
            if (!disable.Succeeded)
            {
                return new SetupResult(
                    false,
                    $"Could not disable {UserUnitName} — left {unitPath} in place so you can retry.",
                    string.IsNullOrWhiteSpace(disable.StandardError)
                        ? "Check `systemctl --user status ydotoold.service`."
                        : disable.StandardError.Trim()
                );
            }
        }

        // Delete our user unit file if we own it. Mirrors the udev-rule
        // ownership guard: a unit a distro package or the user wrote at
        // the same path stays in place. Teardown order: disable → delete
        // file → daemon-reload.
        var removedUnit = false;
        string? unitLeftMessage = null;
        if (File.Exists(unitPath))
        {
            if (!weOwnUnit)
            {
                unitLeftMessage =
                    $"Left {unitPath} in place and untouched — it has no TypeWhisper ownership marker, so its ydotoold service stays enabled.";
            }
            else if (!DesktopDetector.BinaryExists("systemctl"))
            {
                // Fail closed, mirroring the disable gate above: with no
                // systemctl we never disabled the unit, so deleting the file
                // would orphan any enablement symlink. Leave it for the user.
                unitLeftMessage =
                    $"Left {unitPath} in place — systemctl is not available to disable the unit or reload the user manager. Delete it manually once systemctl is back.";
            }
            else
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
                    return new SetupResult(false, $"Could not delete {unitPath}: {ex.Message}");
                }
            }
        }

        if (removedUnit && DesktopDetector.BinaryExists("systemctl"))
        {
            await _runner
                .RunAsync("systemctl", new[] { "--user", "daemon-reload" }, ct: ct)
                .ConfigureAwait(false);
        }

        // Remove the root-owned files we installed — the udev rule and the
        // modules-load entry — in a single privileged call so the user sees at
        // most one auth prompt. Each is ownership-gated independently: both use
        // conventional paths a user or distro package might have written first,
        // so a file lacking our marker is left untouched and reported.
        var leftoverMessages = new List<string>();
        var privilegedRemovals = new List<string>();
        foreach (var path in new[] { UdevRulePath, ModulesLoadPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (!IsFileOwnedByTypeWhisper(path))
            {
                leftoverMessages.Add(
                    $"Left {path} in place — it doesn't carry TypeWhisper's ownership marker, so we won't delete it. Remove it manually if you want to."
                );
                continue;
            }

            if (!DesktopDetector.BinaryExists("pkexec"))
            {
                // Fail closed: the file is ours and still on disk, but without
                // pkexec we can't delete root-owned config. Don't report
                // success while leaving privileged state behind.
                return new SetupResult(
                    false,
                    $"Could not remove {path} — pkexec is not available to delete root-owned config.",
                    $"Remove it manually: sudo rm -f {path}"
                );
            }

            privilegedRemovals.Add(path);
        }

        if (privilegedRemovals.Count > 0)
        {
            // Also unload the uinput module we loaded, so a subsequent fresh
            // install re-exercises the full module-load path rather than
            // finding the device already present. `-r` no-ops harmlessly if
            // the module is builtin or still in use by something else.
            var script =
                "set -e\n"
                + "rm -f " + string.Join(" ", privilegedRemovals) + "\n"
                + "modprobe -r uinput 2>/dev/null || true\n";
            var rm = await _runner
                .RunAsync("pkexec", new[] { "/bin/sh" }, standardInput: script, ct: ct)
                .ConfigureAwait(false);
            if (!rm.Succeeded)
            {
                return new SetupResult(
                    false,
                    $"Could not remove root-owned config: {rm.StandardError.Trim()}"
                );
            }
        }

        _commands.RefreshSnapshot();

        var detail = string.Join(
            "\n",
            new[] { unitLeftMessage }
                .Concat(leftoverMessages)
                .Where(m => !string.IsNullOrWhiteSpace(m))
        );
        return new SetupResult(
            true,
            "ydotool integration removed.",
            string.IsNullOrEmpty(detail) ? null : detail
        );
    }

    /// <summary>
    ///     Absolute path to the user-level systemd unit we install when the
    ///     distro doesn't ship one. Honors <c>XDG_CONFIG_HOME</c>, falling
    ///     back to <c>~/.config</c>. Pure — no disk touch.
    /// </summary>
    internal static string UserUnitFilePath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        return Path.Combine(configHome, "systemd", "user", UserUnitName);
    }

    /// <summary>
    ///     Builds the user-level <c>ydotoold.service</c> unit text. The first
    ///     line carries <see cref="OwnershipMarker" /> so <see cref="RemoveAsync" />
    ///     can confirm we own the file before deleting it. Pure.
    /// </summary>
    internal static string BuildUserUnitContent(string ydotooldPath)
    {
        return "# "
               + OwnershipMarker
               + " — user-level ydotoold service so direct-typing\n"
               + "# works without a system unit. Delete this file to roll back.\n"
               + "[Unit]\n"
               + "Description=ydotool daemon (user) — installed by TypeWhisper\n"
               + "Documentation=https://github.com/ReimuNotMoe/ydotool\n"
               + "After=default.target\n"
               + "\n"
               + "[Service]\n"
               + "Type=simple\n"
               + $"ExecStart={ydotooldPath}\n"
               + "Restart=on-failure\n"
               + "RestartSec=2\n"
               + "\n"
               + "[Install]\n"
               + "WantedBy=default.target\n";
    }

    /// <summary>
    ///     Walks <c>$PATH</c> and returns the absolute path of the named
    ///     binary, or <c>null</c> if it isn't reachable. Mirrors
    ///     <see cref="DesktopDetector.BinaryExists" /> but returns the path —
    ///     kept local to this helper since no other caller needs it.
    /// </summary>
    internal static string? ResolveBinaryPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Bad PATH entry — skip.
            }
        }

        return null;
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

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string pathname, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <summary>
    ///     True only when this process can already read+write <c>/dev/uinput</c>
    ///     (R_OK|W_OK = 6) — the ground-truth signal that the udev rule is
    ///     unnecessary. Running as root is treated as "not accessible": root
    ///     can always write the node, but the real non-root user still needs
    ///     the rule, so we don't let a root-run GUI skip installing it.
    /// </summary>
    private static bool UinputIsAccessible()
    {
        try
        {
            if (geteuid() == 0)
            {
                return false;
            }

            return File.Exists("/dev/uinput") && access("/dev/uinput", 6) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Synchronous probe used by <see cref="IsCurrentlyConfigured" />.
    ///     One-shot subprocess (~5 ms), tight 500 ms ceiling so a hung
    ///     daemon can't wedge the status panel. Blocks the caller — same as
    ///     before the <see cref="IProcessRunner" /> seam; the runner uses
    ///     ConfigureAwait(false) throughout so there is no UI-thread deadlock.
    /// </summary>
    private bool RunSyncProbe(string socketPath)
    {
        var result = _runner
            .RunAsync(
                YdotoolBackend.ExecutableName,
                YdotoolBackend.ProbeArgs(),
                new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath },
                timeout: TimeSpan.FromMilliseconds(500)
            )
            .GetAwaiter()
            .GetResult();
        return result.Succeeded;
    }

    /// <summary>
    ///     Run a no-op ydotool invocation to confirm the daemon can
    ///     actually write to /dev/uinput. Distinguishes "permission denied"
    ///     from other failures so the message can point at the right fix.
    /// </summary>
    private async Task<SetupResult> ProbeYdotoolAsync(string socketPath, CancellationToken ct)
    {
        var probe = await _runner
            .RunAsync(
                YdotoolBackend.ExecutableName,
                YdotoolBackend.ProbeArgs(),
                new Dictionary<string, string> { ["YDOTOOL_SOCKET"] = socketPath },
                ct: ct
            )
            .ConfigureAwait(false);

        if (probe.Succeeded)
        {
            return new SetupResult(true, "ydotool probe succeeded.");
        }

        if (LooksLikePermissionError(probe.StandardError))
        {
            return new SetupResult(
                false,
                "ydotoold can't write to /dev/uinput (permission denied).",
                "On older systems where TAG+=\"uaccess\" doesn't apply, add yourself to the input group and log out / back in:\n"
                + "  sudo usermod -aG input $USER\n"
                + "Then re-open Settings → Text insertion to verify."
            );
        }

        return new SetupResult(
            false,
            "ydotool probe failed.",
            string.IsNullOrWhiteSpace(probe.StandardError)
                ? "Check `journalctl --user -u ydotoold`."
                : probe.StandardError.Trim()
        );
    }

    private static bool LooksLikePermissionError(string stderr)
    {
        return !string.IsNullOrEmpty(stderr)
               && (
                   stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                   || stderr.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
                   || stderr.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
               );
    }

    private async Task<SetupResult> InstallUdevRuleAsync(CancellationToken ct)
    {
        // pkexec is only needed on the udev-rule write path; check it
        // here (the actual point of use) rather than at the top of
        // SetUpAsync — otherwise users whose rule is already installed
        // can't complete setup even though pkexec isn't needed.
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new SetupResult(
                false,
                "pkexec is not available, so the udev rule can't be installed automatically.",
                "Run this manually:\n"
                + $"  sudo tee {ModulesLoadPath} > /dev/null <<'EOF'\n"
                + ModulesLoadContent
                + "EOF\n"
                + $"  sudo tee {UdevRulePath} > /dev/null <<'EOF'\n"
                + UdevRuleContent
                + "EOF\n"
                + "  sudo udevadm control --reload\n"
                // modprobe AFTER the reload so the freshly-written rule is in
                // effect when the module's add-event fires and the device node
                // is created — that's what applies the input group + uaccess ACL.
                + "  sudo modprobe uinput\n"
                + "  sudo udevadm trigger --subsystem-match=misc --action=change\n"
                // Don't hand the user `systemctl --user enable --now ydotoold.service`
                // here: on a clean install no user unit exists yet (we return before
                // EnsureUserUnitExistsAsync runs), so that command would just fail
                // with "Unit ydotoold.service not found". Once the steps above make
                // /dev/uinput accessible, rerunning setup skips the pkexec path
                // entirely and creates + enables + restarts the unit for you.
                + "Then reopen Settings → Text insertion and run setup again — "
                + "TypeWhisper will create and start the ydotoold user service."
            );
        }

        // Pipe content via here-docs, not command-line args, to avoid shell metadata issues.
        // Order: write files → reload udev → modprobe uinput. Loading the module AFTER
        // the reload ensures the add-event fires with the rule already live, applying
        // the input group + uaccess ACL to the newly created device node.
        var script =
            $"set -e\n"
            + $"cat > {ModulesLoadPath} <<'EOF'\n"
            + ModulesLoadContent
            + "EOF\n"
            + $"cat > {UdevRulePath} <<'EOF'\n"
            + UdevRuleContent
            + "EOF\n"
            + "udevadm control --reload\n"
            + "modprobe uinput || true\n"
            + "udevadm trigger --subsystem-match=misc --action=change || true\n";

        var run = await _runner
            .RunAsync("pkexec", new[] { "/bin/sh" }, standardInput: script, ct: ct)
            .ConfigureAwait(false);

        if (!run.Succeeded)
        {
            return new SetupResult(
                false,
                "Could not install udev rule (pkexec failed or was canceled).",
                string.IsNullOrWhiteSpace(run.StandardError)
                    ? run.StandardOutput
                    : run.StandardError
            );
        }

        return new SetupResult(true, "Installed udev rule.");
    }

    /// <summary>
    ///     Ensures a user-level <c>ydotoold.service</c> resolves. If one
    ///     already does (the user, or a distro/AUR package, set one up) we
    ///     respect it and don't overwrite. Otherwise we atomically write our
    ///     own to <see cref="UserUnitFilePath" />. Returns whether the unit
    ///     file was newly written so the caller can decide whether a
    ///     <c>daemon-reload</c> is needed — keeping <see cref="SetUpAsync" />
    ///     free of mutable instance state (this helper is a DI singleton).
    /// </summary>
    private async Task<(SetupResult result, bool wroteUnitFile)> EnsureUserUnitExistsAsync(
        CancellationToken ct
    )
    {
        // `systemctl --user cat` exits 0 only when a unit by this name
        // resolves through the full unit search path — covers a unit the
        // user wrote, or one shipped by a distro/AUR package, wherever it
        // lives. Respect any such unit rather than shadowing it.
        var cat = await _runner
            .RunAsync("systemctl", new[] { "--user", "cat", UserUnitName }, ct: ct)
            .ConfigureAwait(false);
        if (cat.Succeeded)
        {
            return (new SetupResult(true, "A ydotoold user service already exists."), false);
        }

        // No user unit resolves — we must write our own, which needs the
        // daemon's absolute path for ExecStart. Resolve it only here, not up
        // front in SetUpAsync: an already-resolving unit (handled above) may
        // legitimately point ExecStart at a ydotoold outside this process's
        // $PATH, and rejecting that working setup just because we can't find
        // the binary ourselves would be wrong.
        var ydotooldPath = ResolveBinaryPath("ydotoold");
        if (ydotooldPath is null)
        {
            return (
                new SetupResult(
                    false,
                    "ydotoold (the ydotool daemon) is not installed.",
                    "On Fedora the `ydotool` package includes it: sudo dnf install ydotool"
                ),
                false
            );
        }

        var unitPath = UserUnitFilePath();
        try
        {
            // Ownership guard, mirroring the udev-rule check: if a file is
            // already at our path but lacks our marker, the user put it
            // there — don't clobber it.
            if (File.Exists(unitPath) && !IsFileOwnedByTypeWhisper(unitPath))
            {
                return (
                    new SetupResult(
                        false,
                        $"A ydotoold user unit already exists at {unitPath} but doesn't carry TypeWhisper's ownership marker.",
                        "Remove or fix that file manually, then run setup again."
                    ),
                    false
                );
            }

            // Atomic write: temp file + move, same pattern as
            // BrowserAccessibilitySetupHelper.WriteEnvFile.
            Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
            var tempPath = unitPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, BuildUserUnitContent(ydotooldPath), ct)
                .ConfigureAwait(false);
            File.Move(tempPath, unitPath, true);
            return (new SetupResult(true, $"Wrote {unitPath}."), true);
        }
        catch (Exception ex)
        {
            return (
                new SetupResult(
                    false,
                    "Could not write the ydotoold user unit.",
                    ex.Message
                ),
                false
            );
        }
    }

    private async Task<SetupResult> EnableUserUnitAsync(string unit, CancellationToken ct)
    {
        var enable = await _runner
            .RunAsync("systemctl", new[] { "--user", "enable", "--now", unit }, ct: ct)
            .ConfigureAwait(false);
        if (!enable.Succeeded)
        {
            return new SetupResult(
                false,
                $"Could not enable {unit}: {enable.StandardError.Trim()}",
                "If your distro doesn't run user-instance systemd, start the daemon manually:\n"
                + "  nohup ydotoold &\n"
                + "(Note: this will not survive logout.)"
            );
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
            {
                return path;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        return null;
    }

    private bool IsUserUnitActive(string unit)
    {
        var result = _runner
            .RunAsync(
                "systemctl",
                new[] { "--user", "is-active", "--quiet", unit },
                timeout: TimeSpan.FromMilliseconds(500)
            )
            .GetAwaiter()
            .GetResult();
        return result.Succeeded;
    }

    public sealed record Status(
        bool BinaryInstalled,
        bool UdevRulePresent,
        bool SystemdUnitActive,
        bool SocketReachable,
        bool ProbeSucceeded,
        bool UinputAccessible,
        string? SocketPath
    )
    {
        /// <summary>
        ///     True only when every layer is wired AND the daemon can actually
        ///     write to /dev/uinput. <see cref="ProbeSucceeded" /> guards against
        ///     the "socket exists but every keystroke fails EACCES" failure
        ///     mode that happens on older systems where <c>TAG+="uaccess"</c>
        ///     didn't apply and the user isn't in the input group. The udev
        ///     rule is satisfied either by our installed rule
        ///     (<see cref="UdevRulePresent" />) or by the kernel already
        ///     granting access (<see cref="UinputAccessible" />) — setup skips
        ///     installing the rule in the latter case, so it can't be a hard
        ///     requirement here.
        /// </summary>
        public bool IsFullyConfigured =>
            BinaryInstalled
            && SystemdUnitActive
            && SocketReachable
            && ProbeSucceeded
            && (UdevRulePresent || UinputAccessible);
    }

    public sealed record SetupResult(bool Success, string Message, string? Detail = null);
}