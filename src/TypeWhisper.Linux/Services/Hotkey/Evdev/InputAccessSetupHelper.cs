using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     One-click installer for the keyboard-access udev rule that the Wayland
///     evdev global-hotkey backend needs. Writes
///     <c>/etc/udev/rules.d/61-typewhisper-input.rules</c> via <c>pkexec</c>
///     (one admin prompt), then reloads + retriggers udev so the active session
///     gains read access to keyboard event nodes <em>immediately</em> — no
///     logout or reboot.
///     <para>
///         The rule tags keyboard devices with <c>TAG+="uaccess"</c>, the modern
///         systemd-logind primitive that grants the user on the currently active
///         seat access without group membership. <c>GROUP="input"</c> is the
///         fallback for init systems without logind (Devuan, Alpine without
///         elogind); on those the user must additionally join the <c>input</c>
///         group and re-login, which <see cref="GlobalHotkeySetupTask" /> handles.
///     </para>
///     <para>
///         This is strictly narrower than the old "join the input group"
///         approach: the rule is scoped to keyboards
///         (<c>ENV{ID_INPUT_KEYBOARD}=="1"</c>) and to the active session, so it
///         does not expose mice, touchpads, or other input devices, and it does
///         not grant a background/remote session access. Modeled directly on
///         <see cref="Insertion.YdotoolSetupHelper" />, which uses the same
///         primitive for <c>/dev/uinput</c>.
///     </para>
/// </summary>
public sealed class InputAccessSetupHelper
{
    // System config dir holding the udev rule. Always /etc in production. Tests
    // redirect it via SysConfDirOverride so InstallAsync / RemoveAsync stay
    // hermetic instead of reading or writing the host's real /etc.
    //
    // Deliberately NOT environment-controlled: the path is interpolated into a
    // privileged `pkexec /bin/sh` script (the rule write and the removal `rm`),
    // so an env-settable value would be a root command-injection / breakage
    // surface. The override is internal, so only the in-process test (via
    // InternalsVisibleTo) can set it.
    internal static string? SysConfDirOverride { get; set; }

    private static string SysConfDir => SysConfDirOverride ?? "/etc";

    // 61- so it sorts after the distro's 60-* input rules (and our own
    // 60-ydotool.rules) — uaccess tags are additive, but ordering after the
    // base rules keeps the intent clear when reading `udevadm info`.
    internal static string UdevRulePath =>
        Path.Join(SysConfDir, "udev/rules.d/61-typewhisper-input.rules");

    // Marker used by RemoveAsync to confirm we own the file before deleting it —
    // without this we could nuke a rule a user or distro package installed under
    // the same conventional filename.
    internal const string OwnershipMarker = "Installed by TypeWhisper";

    internal const string UdevRuleContent =
        "# "
        + OwnershipMarker
        + " — grants the active local session read access to\n"
        + "# keyboard event nodes so the evdev global-hotkey backend can detect the\n"
        + "# dictation shortcut while other apps are focused. TAG+=\"uaccess\" is the modern\n"
        + "# systemd-logind primitive: it grants the user on the currently active seat\n"
        + "# access without group membership or logout. GROUP=\"input\" is the fallback for\n"
        + "# init systems without logind (Devuan, Alpine without elogind).\n"
        + "SUBSYSTEM==\"input\", ENV{ID_INPUT_KEYBOARD}==\"1\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\"\n";

    private readonly IProcessRunner _runner;

    public InputAccessSetupHelper(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    ///     True when our keyboard-access rule file is on disk — i.e. we've already
    ///     run the install at least once. Distinguishes "we installed the rule and
    ///     only a non-logind re-login remains" from "stale input-group membership
    ///     from the old flow, where installing the rule could grant access now".
    /// </summary>
    public bool IsRuleInstalled()
    {
        return File.Exists(UdevRulePath);
    }

    /// <summary>
    ///     Installs the keyboard-access rule and applies it to the current
    ///     session. On logind systems this makes keyboard event nodes readable
    ///     right away — the caller should re-probe access (see
    ///     <see cref="InputDeviceAccessCheck.HasKeyboardAccess" />) rather than
    ///     trust the file's existence, because non-logind systems still need a
    ///     group join + re-login before the <c>GROUP="input"</c> fallback grants
    ///     access.
    /// </summary>
    public async Task<Result> InstallAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new Result(
                false,
                "pkexec is not available, so the keyboard-access rule can't be installed automatically.",
                ManualInstallCommand()
            );
        }

        // Pipe content via a here-doc, not command-line args, to avoid shell
        // metadata issues. Order: write file → reload udev → retrigger the
        // input subsystem so the rule applies to the keyboards that are already
        // plugged in. The retrigger (action=change) is what removes the reboot:
        // it re-evaluates the rule against live devices and applies the uaccess
        // ACL to the active session now.
        var script =
            "set -e\n"
            + $"cat > {UdevRulePath} <<'EOF'\n"
            + UdevRuleContent
            + "EOF\n"
            + "udevadm control --reload\n"
            + "udevadm trigger --subsystem-match=input --action=change\n"
            // Block until udev has finished applying the rule (the uaccess ACL is
            // set during event processing) so the caller's immediate re-probe of
            // keyboard access sees the granted access rather than racing it.
            // Bounded so a stuck udev can't wedge the setup flow.
            + "udevadm settle --timeout=5 || true\n";

        // Bounded so a hidden/stalled polkit prompt or a stuck privileged command
        // can't wedge the required first-run setup task forever (it runs with
        // CancellationToken.None). Matches the input-group fallback's 2-minute
        // budget — long enough for the user to type a password, short enough to
        // fail back to the manual command.
        var run = await _runner
            .RunAsync(
                "pkexec",
                new[] { "/bin/sh" },
                standardInput: script,
                timeout: TimeSpan.FromMinutes(2),
                ct: ct
            )
            .ConfigureAwait(false);

        if (run.TimedOut)
        {
            return new Result(
                false,
                "Installing the keyboard-access rule timed out waiting for admin authorization.",
                ManualInstallCommand()
            );
        }

        if (!run.Succeeded)
        {
            // pkexec exits 126/127 when the auth dialog is dismissed or denied —
            // surface that distinctly so the caller can offer the manual command.
            if (run.ExitCode is 126 or 127)
            {
                return new Result(
                    false,
                    "Admin authorization was cancelled or denied.",
                    ManualInstallCommand(),
                    Cancelled: true
                );
            }

            return new Result(
                false,
                "Could not install the keyboard-access rule (pkexec failed).",
                string.IsNullOrWhiteSpace(run.StandardError)
                    ? run.StandardOutput
                    : run.StandardError
            );
        }

        return new Result(true, "Installed the keyboard-access rule.");
    }

    /// <summary>
    ///     Removes the keyboard-access rule if (and only if) TypeWhisper installed
    ///     it, then reloads + retriggers udev so the access ACL is revoked from
    ///     the active session. A file at our path that lacks the ownership marker
    ///     is left untouched.
    /// </summary>
    public async Task<Result> RemoveAsync(CancellationToken ct)
    {
        if (!File.Exists(UdevRulePath))
        {
            return new Result(true, "No keyboard-access rule to remove.");
        }

        if (!IsFileOwnedByTypeWhisper(UdevRulePath))
        {
            return new Result(
                true,
                "Keyboard-access rule left in place.",
                $"Left {UdevRulePath} untouched — it doesn't carry TypeWhisper's ownership marker, so we won't delete it. Remove it manually if you want to."
            );
        }

        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            // Fail closed: the file is ours and still on disk, but without pkexec
            // we can't delete root-owned config. Don't report success while
            // leaving privileged state behind.
            return new Result(
                false,
                $"Could not remove {UdevRulePath} — pkexec is not available to delete root-owned config.",
                $"Remove it manually: sudo rm -f {UdevRulePath} && sudo udevadm control --reload && sudo udevadm trigger --subsystem-match=input --action=change"
            );
        }

        var script =
            "set -e\n"
            + $"rm -f {UdevRulePath}\n"
            + "udevadm control --reload\n"
            + "udevadm trigger --subsystem-match=input --action=change\n";

        var rm = await _runner
            .RunAsync(
                "pkexec",
                new[] { "/bin/sh" },
                standardInput: script,
                timeout: TimeSpan.FromMinutes(2),
                ct: ct
            )
            .ConfigureAwait(false);

        if (rm.TimedOut)
        {
            return new Result(
                false,
                "Removing the keyboard-access rule timed out waiting for admin authorization."
            );
        }

        if (!rm.Succeeded)
        {
            return new Result(
                false,
                $"Could not remove the keyboard-access rule: {rm.StandardError.Trim()}"
            );
        }

        return new Result(true, "Keyboard-access rule removed.");
    }

    /// <summary>
    ///     The exact shell command sequence the privileged install runs, shown to
    ///     the user when <c>pkexec</c> is unavailable or as a copyable fallback in
    ///     the Shortcuts panel. Pure — no disk touch.
    /// </summary>
    public static string ManualInstallCommand()
    {
        return $"sudo tee {UdevRulePath} > /dev/null <<'EOF'\n"
               + UdevRuleContent
               + "EOF\n"
               + "sudo udevadm control --reload\n"
               + "sudo udevadm trigger --subsystem-match=input --action=change\n"
               + "sudo udevadm settle --timeout=5\n"
               // Self-correcting fallback. TAG+="uaccess" grants keyboard access on
               // systems with a logind/elogind seat manager (the common case), so
               // this only acts where uaccess is inert — detected directly by the
               // ABSENCE of a seat-manager runtime dir, rather than by probing node
               // readability (which can't tell a readable mouse from a readable
               // keyboard). There it joins the input group and asks for a re-login.
               + "# Only on systems without systemd-logind/elogind (where uaccess is\n"
               + "# inert): join the input group, then log out and back in.\n"
               + "if [ ! -d /run/systemd/seats ] && [ ! -d /run/elogind/seats ]; then\n"
               + "  sudo usermod -aG input \"$USER\"\n"
               + "fi";
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
            // refusing is always safer than erasing privileged config we can't
            // even inspect.
            return false;
        }
    }

    public sealed record Result(
        bool Success,
        string Message,
        string? Detail = null,
        bool Cancelled = false
    );
}
