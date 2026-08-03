using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

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
///         group and re-login, which <see cref="Setup.GlobalHotkeySetupTask" /> handles.
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
    // One list keeps IsSeatManagerPresent and ManualInstallCommand's shell
    // condition agreeing on what "no seat manager" means.
    private static readonly string[] s_seatManagerDirectoryPaths =
    [
        "/run/systemd/seats",
        "/run/elogind/seats",
    ];

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
    private const string OwnershipMarker = "Installed by TypeWhisper";

    internal const int UdevRuleConflictExitCode = 73;
    internal const int UdevRuleSymlinkExitCode = 74;

    private const string UdevRuleConflictToken = "TYPEWHISPER_INPUT_UDEV_RULE_CONFLICT";
    private const string UdevRuleSymlinkToken = "TYPEWHISPER_INPUT_UDEV_RULE_SYMLINK";

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
    public static bool IsRuleInstalled()
    {
        return File.Exists(UdevRulePath);
    }

    /// <summary>
    ///     True when systemd-logind or elogind exposes its seat runtime directory.
    ///     Deliberately a filesystem check, not D-Bus: manager presence is what
    ///     gates the <c>input</c>-group fallback, even if the session can't reach it.
    /// </summary>
    public static bool IsSeatManagerPresent()
    {
        return IsSeatManagerPresent(Directory.Exists);
    }

    internal static bool IsSeatManagerPresent(Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        return Array.Exists(s_seatManagerDirectoryPaths, path => directoryExists(path));
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

        var script = BuildPrivilegedInstallScript();

        // Bounded so a hidden/stalled polkit prompt or a stuck privileged command
        // can't wedge the required first-run setup task forever (it runs with
        // CancellationToken.None). Matches the input-group fallback's 2-minute
        // budget — long enough for the user to type a password, short enough to
        // fail back to the manual command.
        var run = await _runner
            .RunAsync(
                "pkexec",
                ["/bin/sh"],
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

        if (run.Succeeded)
        {
            return new Result(true, "Installed the keyboard-access rule.");
        }

        if (MatchesPrivilegedFailure(run, UdevRuleConflictExitCode, UdevRuleConflictToken))
        {
            return ForeignConfigRefusal();
        }

        if (MatchesPrivilegedFailure(run, UdevRuleSymlinkExitCode, UdevRuleSymlinkToken))
        {
            return SymlinkRefusal();
        }

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

    /// <summary>
    ///     Builds the root-side installation transaction. Ownership, file-type,
    ///     and symlink checks deliberately run inside this script so an
    ///     unprivileged preflight cannot race the privileged write.
    /// </summary>
    private static string BuildPrivilegedInstallScript()
    {
        // Pipe content via a here-doc, not command-line args, to avoid shell
        // metadata issues. The `case` glob anchors the marker match to the first
        // line, mirroring IsFileOwnedByTypeWhisper. The retrigger applies the rule
        // to keyboards that are already plugged in.
        return "set -e\n"
               + $"udev_path='{UdevRulePath}'\n"
               + $"marker='# {OwnershipMarker}'\n"
               + "if [ -L \"$udev_path\" ]; then\n"
               + $"  echo '{UdevRuleSymlinkToken}' >&2\n"
               + $"  exit {UdevRuleSymlinkExitCode}\n"
               + "elif [ -e \"$udev_path\" ]; then\n"
               + "  if [ ! -f \"$udev_path\" ]; then\n"
               + $"    echo '{UdevRuleConflictToken}' >&2\n"
               + $"    exit {UdevRuleConflictExitCode}\n"
               + "  elif first=$(head -n 1 \"$udev_path\") && case \"$first\" in \"$marker\"|\"$marker \"*) true;; *) false;; esac; then\n"
               + "    :\n"
               + "  else\n"
               + $"    echo '{UdevRuleConflictToken}' >&2\n"
               + $"    exit {UdevRuleConflictExitCode}\n"
               + "  fi\n"
               + "fi\n"
               + "cat > \"$udev_path\" <<'EOF'\n"
               + UdevRuleContent
               + "EOF\n"
               + "udevadm control --reload\n"
               + "udevadm trigger --subsystem-match=input --action=change\n"
               // Block until udev has finished applying the rule (the uaccess ACL is
               // set during event processing) so the caller's immediate re-probe of
               // keyboard access sees the granted access rather than racing it.
               // Bounded so a stuck udev can't wedge the setup flow.
               + "udevadm settle --timeout=5 || true\n";
    }

    /// <summary>
    ///     Builds the root-side removal transaction. Re-validates ownership
    ///     immediately before <c>rm</c>, so an unprivileged marker check that went
    ///     stale during the auth prompt cannot delete foreign config that replaced
    ///     ours.
    /// </summary>
    private static string BuildPrivilegedRemoveScript()
    {
        return "set -e\n"
               + $"udev_path='{UdevRulePath}'\n"
               + $"marker='# {OwnershipMarker}'\n"
               + "if [ -L \"$udev_path\" ]; then\n"
               + $"  echo '{UdevRuleSymlinkToken}' >&2\n"
               + $"  exit {UdevRuleSymlinkExitCode}\n"
               + "elif [ -e \"$udev_path\" ]; then\n"
               + "  if [ ! -f \"$udev_path\" ]; then\n"
               + $"    echo '{UdevRuleConflictToken}' >&2\n"
               + $"    exit {UdevRuleConflictExitCode}\n"
               + "  elif first=$(head -n 1 \"$udev_path\") && case \"$first\" in \"$marker\"|\"$marker \"*) true;; *) false;; esac; then\n"
               + "    rm -f \"$udev_path\"\n"
               + "  else\n"
               + $"    echo '{UdevRuleConflictToken}' >&2\n"
               + $"    exit {UdevRuleConflictExitCode}\n"
               + "  fi\n"
               + "fi\n"
               + "udevadm control --reload\n"
               + "udevadm trigger --subsystem-match=input --action=change\n";
    }

    private static bool MatchesPrivilegedFailure(
        ProcessRunResult run,
        int exitCode,
        string token
    )
    {
        return run.ExitCode == exitCode
               && run.StandardError.Contains(token, StringComparison.Ordinal);
    }

    private static Result ForeignConfigRefusal()
    {
        return new Result(
            false,
            Loc.Instance.GetString("Shortcuts.KeyboardAccessForeignConfigRefused", UdevRulePath),
            Loc.Instance["Shortcuts.KeyboardAccessForeignConfigRefusedDetail"],
            Refused: true
        );
    }

    private static Result SymlinkRefusal()
    {
        return new Result(
            false,
            Loc.Instance.GetString("Shortcuts.KeyboardAccessSymlinkRefused", UdevRulePath),
            Loc.Instance["Shortcuts.KeyboardAccessSymlinkRefusedDetail"],
            Refused: true
        );
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

        var rm = await _runner
            .RunAsync(
                "pkexec",
                ["/bin/sh"],
                standardInput: BuildPrivilegedRemoveScript(),
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

        // A refusal exit means the root-side re-validation found a foreign file or
        // symlink that replaced ours while the auth prompt was open.
        if (MatchesPrivilegedFailure(rm, UdevRuleConflictExitCode, UdevRuleConflictToken))
        {
            return ForeignConfigRefusal();
        }

        if (MatchesPrivilegedFailure(rm, UdevRuleSymlinkExitCode, UdevRuleSymlinkToken))
        {
            return SymlinkRefusal();
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
    ///     A copyable shell command that installs the rule, shown to the user when
    ///     <c>pkexec</c> is unavailable or as a fallback in the Shortcuts panel. Pure
    ///     — no disk touch. The entire sequence runs in a single <c>sudo sh -c</c>
    ///     (one password prompt) under one <c>set -e</c>, so the symlink /
    ///     non-regular-file / foreign-marker guard fails closed: on refusal the udev
    ///     reload/trigger and the input-group fallback never run, matching the
    ///     automated path.
    /// </summary>
    public static string ManualInstallCommand()
    {
        return "sudo sh -c '\n"
               + "set -e\n"
               + $"udev_path=\"{UdevRulePath}\"\n"
               + $"marker=\"# {OwnershipMarker}\"\n"
               + "if [ -L \"$udev_path\" ]; then\n"
               + "  echo \"Refusing: $udev_path is a symbolic link — move it aside first.\" >&2; exit 1\n"
               + "elif [ -e \"$udev_path\" ] && [ ! -f \"$udev_path\" ]; then\n"
               + "  echo \"Refusing: $udev_path is not a regular file — move it aside first.\" >&2; exit 1\n"
               + "elif [ -f \"$udev_path\" ]; then\n"
               + "  first=$(head -n 1 \"$udev_path\")\n"
               + "  case \"$first\" in \"$marker\"|\"$marker \"*) : ;; *)\n"
               + "    echo \"Refusing: $udev_path is foreign config — move it aside first.\" >&2; exit 1 ;;\n"
               + "  esac\n"
               + "fi\n"
               + "cat > \"$udev_path\" <<'\"'\"'EOF'\"'\"'\n"
               + UdevRuleContent
               + "EOF\n"
               + "udevadm control --reload\n"
               + "udevadm trigger --subsystem-match=input --action=change\n"
               // || true so a settle timeout under set -e can't stop the script before the
               // input-group fallback below, matching the privileged install path.
               + "udevadm settle --timeout=5 || true\n"
               // Self-correcting fallback. TAG+="uaccess" grants keyboard access on
               // systems with a logind/elogind seat manager (the common case), so
               // this only acts where uaccess is inert — detected directly by the
               // ABSENCE of a seat-manager runtime dir, rather than by probing node
               // readability (which can't tell a readable mouse from a readable
               // keyboard). There it joins the input group and asks for a re-login.
               + "# Only on systems without systemd-logind/elogind (where uaccess is\n"
               + "# inert): join the input group, then log out and back in.\n"
               + $"if {SeatManagerAbsentShellCondition()}; then\n"
               // ${SUDO_USER:-$USER}: the body runs as root under sudo, so bare
               // $USER would join root, not the invoking user.
               + "  usermod -aG input \"${SUDO_USER:-$USER}\"\n"
               + "fi\n"
               + "'";
    }

    private static string SeatManagerAbsentShellCondition()
    {
        return string.Join(
            " && ",
            s_seatManagerDirectoryPaths.Select(path => $"[ ! -d \"{path}\" ]")
        );
    }

    private static bool IsFileOwnedByTypeWhisper(string path)
    {
        try
        {
            // Only a first-line "# <marker>" header counts — bare or followed by a
            // space. Mid-body mentions and longer prefixes ("# Installed by
            // TypeWhisperer") are foreign. Mirrors the privileged scripts' `case` glob.
            using var reader = new StreamReader(path);
            var firstLine = reader.ReadLine();
            const string header = "# " + OwnershipMarker;
            return firstLine is not null
                   && (firstLine == header
                       || firstLine.StartsWith(header + " ", StringComparison.Ordinal));
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
        bool Cancelled = false,
        // Set when we refused to touch a foreign file or symlink at our path.
        // Callers must surface Message/Detail as-is and must NOT offer the manual
        // install command aimed at the very file the guard just protected.
        bool Refused = false
    );
}
