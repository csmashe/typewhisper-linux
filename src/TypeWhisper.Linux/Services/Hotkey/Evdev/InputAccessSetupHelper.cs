using System.Globalization;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.ManagedArtifacts;

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
public sealed partial class InputAccessSetupHelper
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
    internal static string? RootManagedArtifactStateRootOverride { get; set; }
    internal static string? InputGroupGrantStateDirectoryOverride { get; set; }
    internal static Func<(uint Uid, string UserName)>? CurrentIdentityOverride { get; set; }
    internal static string[]? SeatManagerDirectoryPathsOverride { get; set; }

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
    private const string ActivationFailureToken = "TYPEWHISPER_INPUT_ACTIVATION_FAILED";
    private const string InputGroupGrantUnsafeToken =
        "TYPEWHISPER_INPUT_GROUP_GRANT_STATE_UNSAFE";
    private const string InputGroupAddedToken = "TYPEWHISPER_INPUT_GROUP_ADDED";
    private const string InputGroupPreexistingToken =
        "TYPEWHISPER_INPUT_GROUP_PREEXISTING";
    private const string InputGroupRevokedToken = "TYPEWHISPER_INPUT_GROUP_REVOKED";

    internal const int InputGroupGrantUnsafeExitCode = 75;

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
    ///     True only when the rule path is a regular, non-symlink file carrying
    ///     TypeWhisper's anchored first-line ownership marker. This is a UI probe;
    ///     the privileged removal transaction still revalidates before deleting.
    /// </summary>
    public static bool IsOwnedRuleInstalled()
    {
        try
        {
            var info = new FileInfo(UdevRulePath);
            info.Refresh();
            return info is { Exists: true, LinkTarget: null }
                   && !info.Attributes.HasFlag(FileAttributes.Directory)
                   && IsFileOwnedByTypeWhisper(UdevRulePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     True when the current uid's provenance record is a regular, non-symlink
    ///     file whose content matches the current identity's owned or
    ///     pending-add state.
    /// </summary>
    internal static bool HasMatchingInputGroupGrantProvenance()
    {
        try
        {
            var identity = GetCurrentIdentity();
            var directory = new DirectoryInfo(InputGroupGrantStateDirectory);
            directory.Refresh();
            if (!directory.Exists || directory.LinkTarget is not null)
            {
                return false;
            }

            var path = InputGroupGrantRecordPath(identity);
            var record = new FileInfo(path);
            record.Refresh();
            if (!record.Exists
                || record.LinkTarget is not null
                || record.Attributes.HasFlag(FileAttributes.Directory))
            {
                return false;
            }

            var content = File.ReadAllText(path);
            return content == InputGroupGrantRecordContent("owned", identity)
                   || content == InputGroupGrantRecordContent("pending-add", identity);
        }
        catch
        {
            return false;
        }
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
        return Array.Exists(SeatManagerDirectoryPaths, path => directoryExists(path));
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

        if (run.StandardError.Contains(ActivationFailureToken, StringComparison.Ordinal))
        {
            return new Result(
                false,
                Loc.Instance["Shortcuts.KeyboardAccessActivationFailed"],
                run.StandardError.Trim()
            );
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
    private static string BuildPrivilegedInstallScript(bool includeGroupFallback = false)
    {
        var afterCommit =
            "if ! udevadm control --reload; then\n"
            + $"  echo '{ActivationFailureToken}' >&2; exit 80\n"
            + "fi\n"
            + "if ! udevadm trigger --subsystem-match=input --action=change; then\n"
            + $"  echo '{ActivationFailureToken}' >&2; exit 80\n"
            + "fi\n"
            + "udevadm settle --timeout=5 || true\n";
        if (includeGroupFallback)
        {
            afterCommit +=
                "# Only on systems without systemd-logind/elogind (where uaccess is inert):\n"
                + $"if {SeatManagerAbsentShellCondition()}; then\n"
                + BuildInputGroupGrantAddShellFragment()
                + "fi\n";
        }

        return PrivilegedManagedFileTransaction.BuildInstallScript(
            RootStateRoot,
            [RootSpec],
            afterCommit
        );
    }

    /// <summary>
    ///     Builds the root-side removal transaction. Re-validates ownership
    ///     immediately before <c>rm</c>, so an unprivileged marker check that went
    ///     stale during the auth prompt cannot delete foreign config that replaced
    ///     ours.
    /// </summary>
    private static string BuildPrivilegedRemoveScript(bool removeManagedGroupGrant)
    {
        return PrivilegedManagedFileTransaction.BuildRemoveScript(
            RootStateRoot,
            [RootSpec],
            "udevadm control --reload\n"
            + "udevadm trigger --subsystem-match=input --action=change\n"
            + (removeManagedGroupGrant
                ? BuildInputGroupGrantRemoveShellFragment()
                : string.Empty)
        );
    }

    private static string BuildPrivilegedGroupFallbackScript()
    {
        // The supplementary group grant is independent of the udev-rule file,
        // but shares the same root-side lock with its managed transaction.
        return BuildPrivilegedLockPrefix() + BuildInputGroupGrantAddShellFragment();
    }

    private static string BuildPrivilegedGroupOnlyRemoveScript()
    {
        return BuildPrivilegedLockPrefix() + BuildInputGroupGrantRemoveShellFragment();
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
    ///     Adds the current user to the input group on a non-logind host while
    ///     recording exact root-owned provenance. The root-side fragment rechecks
    ///     membership before writing pending state or invoking usermod.
    /// </summary>
    public async Task<Result> AddToInputGroupFallbackAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("pkexec"))
        {
            return new Result(
                false,
                "pkexec is not available, so input-group access can't be configured automatically.",
                ManualInstallCommand()
            );
        }

        string script;
        try
        {
            script = BuildPrivilegedGroupFallbackScript();
        }
        catch (InvalidOperationException ex)
        {
            return new Result(false, ex.Message);
        }

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
                "Adding input-group access timed out waiting for admin authorization.",
                ManualInstallCommand()
            );
        }

        if (run.Succeeded)
        {
            return new Result(
                true,
                "Input-group access is configured.",
                GroupMembershipAdded: run.StandardOutput.Contains(
                    InputGroupAddedToken,
                    StringComparison.Ordinal
                )
            );
        }

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
            "Could not configure input-group access.",
            string.IsNullOrWhiteSpace(run.StandardError)
                ? run.StandardOutput
                : run.StandardError
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
        var entryExists = EntryExistsIncludingSymlink(UdevRulePath);
        var ownedRule = IsOwnedRuleInstalled();
        var managedGroupGrant = HasMatchingInputGroupGrantProvenance();
        var pkexecAvailable = DesktopDetector.BinaryExists("pkexec");
        if (!entryExists && !managedGroupGrant && !pkexecAvailable)
        {
            return new Result(true, "No managed keyboard access to remove.");
        }

        if (!pkexecAvailable)
        {
            // Fail closed: without pkexec we can't delete root-owned config, so don't
            // report success while it's still there. Existence alone is not ownership —
            // the privileged path checks the marker before deleting, so manual guidance
            // must too, or we'd tell the user to erase a distro-installed rule.
            if (!ownedRule && !managedGroupGrant)
            {
                return new Result(
                    false,
                    $"Could not remove {UdevRulePath} — pkexec is not available to delete root-owned config.",
                    $"{UdevRulePath} exists but does not carry TypeWhisper's ownership marker, so it may belong to your distribution. Inspect it yourself before deleting anything: sudo cat {UdevRulePath}"
                );
            }

            return new Result(
                false,
                "Could not revoke managed keyboard access automatically because pkexec is not available.",
                ManualRemoveCommand(ownedRule, managedGroupGrant)
            );
        }

        // A foreign rule is not ours to touch. If matching group provenance also
        // exists, revoke that independent grant without aiming a command at the
        // foreign file. With no managed grant, retain privileged revalidation so
        // direct callers still receive the established foreign-file refusal.
        var includeRule = ownedRule || !managedGroupGrant;
        var script = includeRule
            ? BuildPrivilegedRemoveScript(managedGroupGrant)
            : BuildPrivilegedGroupOnlyRemoveScript();

        var rm = await _runner
            .RunAsync(
                "pkexec",
                ["/bin/sh"],
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

        // A refusal exit means the root-side re-validation found a foreign file or
        // symlink that replaced ours while the auth prompt was open. The managed
        // group grant is independent, so retry just that revoke under the shared
        // lock instead of leaving it blocked by the rule conflict.
        Result? refusal = null;
        if (MatchesPrivilegedFailure(rm, UdevRuleConflictExitCode, UdevRuleConflictToken))
        {
            refusal = ForeignConfigRefusal();
        }
        else if (MatchesPrivilegedFailure(rm, UdevRuleSymlinkExitCode, UdevRuleSymlinkToken))
        {
            refusal = SymlinkRefusal();
        }

        if (refusal is not null)
        {
            if (!managedGroupGrant)
            {
                return refusal;
            }

            var groupRemoval = await _runner
                .RunAsync(
                    "pkexec",
                    ["/bin/sh"],
                    standardInput: BuildPrivilegedGroupOnlyRemoveScript(),
                    timeout: TimeSpan.FromMinutes(2),
                    ct: ct
                )
                .ConfigureAwait(false);
            if (groupRemoval.Succeeded)
            {
                return refusal with
                {
                    RequiresRelogin = true,
                    GroupRevocationCompleted = true,
                };
            }

            var groupFailure = groupRemoval.TimedOut
                ? "The independent input-group revoke timed out."
                : "The independent input-group revoke failed: "
                  + (string.IsNullOrWhiteSpace(groupRemoval.StandardError)
                      ? groupRemoval.StandardOutput.Trim()
                      : groupRemoval.StandardError.Trim());
            return refusal with { GroupRevocationFailure = groupFailure };
        }

        if (rm.StandardError.Contains(ActivationFailureToken, StringComparison.Ordinal))
        {
            return new Result(
                false,
                Loc.Instance["Shortcuts.KeyboardAccessActivationFailed"],
                rm.StandardError.Trim()
            );
        }

        if (!rm.Succeeded)
        {
            return new Result(
                false,
                $"Could not remove the keyboard-access rule: {rm.StandardError.Trim()}"
            );
        }

        return new Result(
            true,
            "Managed keyboard access removed.",
            RequiresRelogin: managedGroupGrant
        );
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
        string body;
        try
        {
            body = BuildPrivilegedInstallScript(includeGroupFallback: true);
        }
        catch (InvalidOperationException)
        {
            // The udev rule itself does not depend on the current identity. If the
            // username cannot be represented safely, omit only the non-logind
            // input-group fallback instead of making every manual-command caller fail.
            body = BuildPrivilegedInstallScript();
        }

        return "sudo sh -c " + PrivilegedManagedFileTransaction.QuoteAsShCArgument(body);
    }

    private static string ManualRemoveCommand(bool includeRule, bool removeManagedGroupGrant)
    {
        var commands = new List<string>();
        if (includeRule)
        {
            commands.Add(ToManualSudoCommand(BuildPrivilegedRemoveScript(false)));
        }

        if (removeManagedGroupGrant)
        {
            try
            {
                commands.Add(ToManualSudoCommand(BuildPrivilegedGroupOnlyRemoveScript()));
            }
            catch (InvalidOperationException)
            {
                // The independent rule-removal command above remains useful. A
                // group-only request has no safe command without a valid identity.
            }
        }

        return commands.Count == 0
            ? "Manual input-group revocation is unavailable because the current username cannot be represented safely."
            : string.Join('\n', commands);
    }

    private static string ToManualSudoCommand(string body)
    {
        return "sudo sh -c " + PrivilegedManagedFileTransaction.QuoteAsShCArgument(body);
    }

    private static string SeatManagerAbsentShellCondition()
    {
        return string.Join(
            " && ",
            SeatManagerDirectoryPaths.Select(
                path =>
                    $"[ ! -d {PrivilegedManagedFileTransaction.QuoteAsShCArgument(path)} ]"
            )
        );
    }

    private static string BuildInputGroupGrantAddShellFragment()
    {
        var identity = GetCurrentIdentity();
        var common = BuildInputGroupGrantCommonShell(identity);
        return common
               + $$"""
            if input_group_member; then
              echo '{{InputGroupPreexistingToken}}'
            else
              ensure_input_group_grant_directory
              write_input_group_grant_record "$input_group_pending_content"
              if usermod -aG input -- "$input_group_user"; then
                write_input_group_grant_record "$input_group_owned_content"
                echo '{{InputGroupAddedToken}}'
              else
                status=$?
                if ! input_group_member; then
                  rm -f "$input_group_record"
                fi
                exit "$status"
              fi
            fi
            """
               + "\n";
    }

    private static string BuildInputGroupGrantRemoveShellFragment()
    {
        var identity = GetCurrentIdentity();
        var common = BuildInputGroupGrantCommonShell(identity);
        return common
               + $$"""
            classify_input_group_grant_record
            if [ "$input_group_record_state" != managed ]; then
              echo '{{InputGroupGrantUnsafeToken}}' >&2
              exit {{InputGroupGrantUnsafeExitCode}}
            fi
            if input_group_member; then
              if command -v gpasswd >/dev/null 2>&1; then
                if gpasswd -d "$input_group_user" input >/dev/null; then
                  :
                else
                  status=$?
                  echo "gpasswd -d exited with status $status" >&2
                  exit "$status"
                fi
              elif usermod -rG input -- "$input_group_user"; then
                :
              else
                status=$?
                echo "usermod -rG exited with status $status" >&2
                exit "$status"
              fi
            fi
            classify_input_group_grant_record
            if [ "$input_group_record_state" != managed ]; then
              echo '{{InputGroupGrantUnsafeToken}}' >&2
              exit {{InputGroupGrantUnsafeExitCode}}
            fi
            rm -f "$input_group_record"
            echo '{{InputGroupRevokedToken}}'
            """
               + "\n";
    }

    private static string BuildInputGroupGrantCommonShell((uint Uid, string UserName) identity)
    {
        var recordPath = InputGroupGrantRecordPath(identity);
        var pending = InputGroupGrantRecordContent("pending-add", identity);
        var owned = InputGroupGrantRecordContent("owned", identity);
        Func<string, string> quote = PrivilegedManagedFileTransaction.QuoteAsShCArgument;
        return $$"""
            input_group_grant_dir={{quote(InputGroupGrantStateDirectory)}}
            input_group_record={{quote(recordPath)}}
            input_group_uid={{quote(identity.Uid.ToString(CultureInfo.InvariantCulture))}}
            input_group_user={{quote(identity.UserName)}}
            input_group_pending_content={{quote(pending)}}
            input_group_owned_content={{quote(owned)}}

            ensure_input_group_grant_directory() {
              if [ -L "$input_group_grant_dir" ] || { [ -e "$input_group_grant_dir" ] && [ ! -d "$input_group_grant_dir" ]; }; then
                echo '{{InputGroupGrantUnsafeToken}}' >&2
                exit {{InputGroupGrantUnsafeExitCode}}
              fi
              mkdir -p "$input_group_grant_dir"
              if [ -L "$input_group_grant_dir" ] || [ ! -d "$input_group_grant_dir" ]; then
                echo '{{InputGroupGrantUnsafeToken}}' >&2
                exit {{InputGroupGrantUnsafeExitCode}}
              fi
              chown root:root "$input_group_grant_dir"
              chmod 0755 "$input_group_grant_dir"
              [ "$(stat -c '%a' "$input_group_grant_dir")" = 755 ]
            }

            classify_input_group_grant_record() {
              input_group_record_state=absent
              if [ ! -e "$input_group_grant_dir" ] && [ ! -L "$input_group_grant_dir" ]; then
                return 0
              fi
              if [ -L "$input_group_grant_dir" ] || [ ! -d "$input_group_grant_dir" ]; then
                echo '{{InputGroupGrantUnsafeToken}}' >&2
                exit {{InputGroupGrantUnsafeExitCode}}
              fi
              if [ ! -e "$input_group_record" ] && [ ! -L "$input_group_record" ]; then
                return 0
              fi
              if [ -L "$input_group_record" ] || [ ! -f "$input_group_record" ]; then
                echo '{{InputGroupGrantUnsafeToken}}' >&2
                exit {{InputGroupGrantUnsafeExitCode}}
              fi
              input_group_actual=$(cat "$input_group_record")
              if [ "$input_group_actual" = "$input_group_pending_content" ] || [ "$input_group_actual" = "$input_group_owned_content" ]; then
                input_group_record_state=managed
              else
                input_group_record_state=foreign
              fi
            }

            write_input_group_grant_record() {
              ensure_input_group_grant_directory
              classify_input_group_grant_record
              if [ "$input_group_record_state" = foreign ]; then
                echo '{{InputGroupGrantUnsafeToken}}' >&2
                exit {{InputGroupGrantUnsafeExitCode}}
              fi
              input_group_stage=$(mktemp "$input_group_grant_dir/.$input_group_uid.XXXXXX")
              printf '%s' "$1" > "$input_group_stage"
              chown root:root "$input_group_stage"
              chmod 0644 "$input_group_stage"
              [ "$(stat -c '%a' "$input_group_stage")" = 644 ]
              mv -f "$input_group_stage" "$input_group_record"
            }

            input_group_member() {
              if input_group_names=$(id -nG -- "$input_group_user"); then
                :
              else
                echo 'Could not look up input-group membership.' >&2
                exit 76
              fi
              set -f
              for input_group_name in $input_group_names; do
                if [ "$input_group_name" = input ]; then
                  set +f
                  return 0
                fi
              done
              set +f
              return 1
            }

            """
               + "\n";
    }

    private static string BuildPrivilegedLockPrefix()
    {
        return PrivilegedManagedFileTransaction.BuildLockPrefix(RootStateRoot);
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

    private static string RootStateRoot =>
        RootManagedArtifactStateRootOverride
        ?? "/var/lib/typewhisper/managed-artifacts";

    internal static string InputGroupGrantStateDirectory =>
        InputGroupGrantStateDirectoryOverride
        ?? "/var/lib/typewhisper/input-group-grants";

    internal static string CurrentUserName => GetCurrentIdentity().UserName;

    internal static string CurrentInputGroupGrantRecordPath =>
        InputGroupGrantRecordPath(GetCurrentIdentity());

    private static string[] SeatManagerDirectoryPaths =>
        SeatManagerDirectoryPathsOverride ?? s_seatManagerDirectoryPaths;

    private static (uint Uid, string UserName) GetCurrentIdentity()
    {
        var identity = CurrentIdentityOverride?.Invoke() ?? (LibcGetEUid(), Environment.UserName);
        if (string.IsNullOrWhiteSpace(identity.UserName)
            || identity.UserName.StartsWith("-", StringComparison.Ordinal)
            || identity.UserName.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(
                "The current username cannot be represented safely in input-group provenance."
            );
        }

        return identity;
    }

    private static string InputGroupGrantRecordPath((uint Uid, string UserName) identity)
    {
        return Path.Join(
            InputGroupGrantStateDirectory,
            identity.Uid.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static string InputGroupGrantRecordContent(
        string state,
        (uint Uid, string UserName) identity
    )
    {
        return $"state={state}\nuid={identity.Uid.ToString(CultureInfo.InvariantCulture)}\nusername={identity.UserName}";
    }

    private static PrivilegedManagedFileSpec RootSpec =>
        new(
            "evdev-udev-rule",
            UdevRulePath,
            UdevRuleContent,
            UdevRuleConflictExitCode,
            UdevRuleConflictToken,
            UdevRuleSymlinkExitCode,
            UdevRuleSymlinkToken
        );

    private static bool EntryExistsIncludingSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists || info.LinkTarget is not null || Directory.Exists(path);
        }
        catch
        {
            return true;
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
        bool Refused = false,
        bool RequiresRelogin = false,
        bool GroupMembershipAdded = false,
        bool GroupRevocationCompleted = false,
        string? GroupRevocationFailure = null
    );

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint LibcGetEUid();
}
