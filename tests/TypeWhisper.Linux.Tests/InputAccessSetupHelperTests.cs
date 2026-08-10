// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read/WriteAllText is deliberate in these test assertions; the async overload would only add await noise with no benefit off the hot path.
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the pure / hermetic surface of <see cref="InputAccessSetupHelper" />
///     — rule content, ownership marker, the install-script / manual-command
///     shape, and <see cref="InputAccessSetupHelper.RemoveAsync" />'s ownership
///     guard. The privileged pkexec path runs through the injected
///     <see cref="IProcessRunner" /> seam; the udev rule path is redirected into a
///     temp dir via <see cref="InputAccessSetupHelper.SysConfDirOverride" /> so the
///     tests never touch the host's real /etc.
/// </summary>
public sealed class InputAccessSetupHelperTests
{
    [Fact]
    public void RuleContent_carries_ownership_marker_and_keyboard_scoped_uaccess()
    {
        const string content = InputAccessSetupHelper.UdevRuleContent;

        Assert.StartsWith("# Installed by TypeWhisper", content.Split('\n')[0]);
        // Keyboard-scoped (not all input devices), uaccess primary, group fallback.
        Assert.Contains("ENV{ID_INPUT_KEYBOARD}==\"1\"", content);
        Assert.Contains("TAG+=\"uaccess\"", content);
        Assert.Contains("GROUP=\"input\"", content);
        Assert.EndsWith("\n", content);
    }

    [Fact]
    public void UdevRulePath_lives_under_the_overridden_sysconf_dir()
    {
        using var env = new SysConfEnvironment();

        Assert.Equal(
            Path.Join(env.Dir, "udev/rules.d/61-typewhisper-input.rules"),
            InputAccessSetupHelper.UdevRulePath
        );
    }

    [Fact]
    public void IsOwnedRuleInstalled_rejects_foreign_regular_file()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule("# Managed by another application\n");

        Assert.False(InputAccessSetupHelper.IsOwnedRuleInstalled());
    }

    [Fact]
    public void IsOwnedRuleInstalled_rejects_symlink_even_when_target_has_marker()
    {
        using var env = new SysConfEnvironment();
        var target = Path.Join(env.Dir, "owned-target.rules");
        File.WriteAllText(target, InputAccessSetupHelper.UdevRuleContent);
        File.CreateSymbolicLink(InputAccessSetupHelper.UdevRulePath, target);

        Assert.False(InputAccessSetupHelper.IsOwnedRuleInstalled());
    }

    [Fact]
    public void ManualInstallCommand_writes_the_rule_and_reloads_and_triggers_udev()
    {
        using var env = new SysConfEnvironment();

        var cmd = InputAccessSetupHelper.ManualInstallCommand();

        Assert.Contains(InputAccessSetupHelper.UdevRulePath, cmd);
        Assert.Contains("udevadm control --reload", cmd);
        Assert.Contains("udevadm trigger --subsystem-match=input --action=change", cmd);
        // The rule body is embedded so the user can paste it verbatim.
        Assert.Contains("TAG+=\"uaccess\"", cmd);
        // The non-logind input-group fallback is present but GUARDED on the absence
        // of a logind/elogind seat manager, so logind users (where uaccess already
        // grants keyboard access) don't join the group needlessly when pasting.
        Assert.Contains("usermod -aG input", cmd);
        Assert.Contains("/run/systemd/seats", cmd);
        Assert.Contains("/run/elogind/seats", cmd);
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("-bad-name")]
    public void ManualInstallCommand_with_unrepresentable_identity_is_non_throwing_and_rule_only(
        string userName
    )
    {
        using var env = new SysConfEnvironment();
        InputAccessSetupHelper.CurrentIdentityOverride = () => (4242, userName);

        var command = InputAccessSetupHelper.ManualInstallCommand();

        Assert.Contains(InputAccessSetupHelper.UdevRulePath, command);
        Assert.Contains("udevadm control --reload", command);
        Assert.Contains(
            "udevadm trigger --subsystem-match=input --action=change",
            command
        );
        Assert.DoesNotContain("usermod -aG input", command);
        Assert.DoesNotContain("gpasswd", command);
    }

    [Fact]
    public void BuildLockPrefix_matches_the_pre_extraction_transaction_prefix()
    {
        const string stateRoot = "/var/lib/typewhisper/managed-artifacts";
        const string expected =
            "set -eu\n"
            + "umask 022\n"
            + "if ! command -v flock >/dev/null 2>&1; then\n"
            + "  echo 'TYPEWHISPER_FLOCK_UNAVAILABLE: flock is required for TypeWhisper managed root files' >&2\n"
            + "  exit 72\n"
            + "fi\n"
            + "state_root='/var/lib/typewhisper/managed-artifacts'\n"
            + "if [ -L \"$state_root\" ] || { [ -e \"$state_root\" ] && [ ! -d \"$state_root\" ]; }; then\n"
            + "  echo 'TYPEWHISPER_ROOT_STATE_UNSAFE' >&2\n"
            + "  exit 72\n"
            + "fi\n"
            + "mkdir -p \"$state_root\"\n"
            + "chmod 0700 \"$state_root\"\n"
            + "if [ -L \"$state_root\" ] || [ ! -d \"$state_root\" ]; then\n"
            + "  echo 'TYPEWHISPER_ROOT_STATE_UNSAFE' >&2\n"
            + "  exit 72\n"
            + "fi\n"
            + "exec 9>\"$state_root/transaction.lock\"\n"
            + "chmod 0600 \"$state_root/transaction.lock\"\n"
            + "flock -x 9\n\n";

        Assert.Equal(expected, PrivilegedManagedFileTransaction.BuildLockPrefix(stateRoot));
    }

    [Fact]
    public void ManualInstallCommand_records_the_non_logind_group_grant()
    {
        using var env = new SysConfEnvironment();
        env.ForceNoSeatManager();

        var exit = RunManualWriteBlock();

        Assert.Equal(0, exit);
        Assert.Equal(
            "state=owned\nuid=4242\nusername=typewhisper-test",
            File.ReadAllText(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath)
        );
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("/run/systemd/seats", true)]
    [InlineData("/run/elogind/seats", true)]
    public void SeatManagerDetection_uses_the_manual_commands_runtime_directories(
        string? existingDirectory,
        bool expected
    )
    {
        var result = InputAccessSetupHelper.IsSeatManagerPresent(
            path => path == existingDirectory
        );

        Assert.Equal(expected, result);
    }

    // The manual command can't rely on the pkexec-script guards, so it fronts its
    // own write with the same symlink / non-regular / foreign-marker checks. These
    // tests execute it against a temp path to prove it refuses foreign targets.
    // The rewrite path has its own test (write_block_writes_an_absent_rule); every case
    // here is a refusal.
    [Theory]
    [InlineData("# Managed by another application\nfoo\n")] // foreign header
    [InlineData("# Installed by TypeWhisperer\nfoo\n")] // prefix-only, not ours
    [InlineData("# Installed by TypeWhisper — old header\nold\n")] // customized-owned
    public void ManualInstallCommand_write_block_refuses_foreign_targets(string existing)
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(existing);

        var exit = RunManualWriteBlock();

        Assert.NotEqual(0, exit);
        Assert.Equal(existing, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public void ManualInstallCommand_write_block_refuses_a_symlink_without_following_it()
    {
        using var env = new SysConfEnvironment();
        var linkPath = InputAccessSetupHelper.UdevRulePath;
        var targetPath = Path.Join(env.Dir, "foreign-target.rules");
        const string targetContent = "# Foreign symlink target\n";
        File.WriteAllText(targetPath, targetContent);
        File.CreateSymbolicLink(linkPath, targetPath);

        var exit = RunManualWriteBlock();

        Assert.NotEqual(0, exit);
        Assert.Equal(targetContent, File.ReadAllText(targetPath));
        Assert.Equal(targetPath, new FileInfo(linkPath).LinkTarget);
    }

    [Fact]
    public void ManualInstallCommand_write_block_writes_an_absent_rule()
    {
        using var env = new SysConfEnvironment();

        var exit = RunManualWriteBlock();

        Assert.Equal(0, exit);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleContent,
            File.ReadAllText(InputAccessSetupHelper.UdevRulePath)
        );
    }

    // Runs the manual command verbatim with sudo neutralized (a passthrough shim)
    // and udevadm/usermod stubbed. The shared `set -e` makes the exit code reflect
    // guard refusal (non-zero) versus a successful install (0).
    private static int RunManualWriteBlock()
    {
        var shimDir = Path.Join(Path.GetTempPath(), $"tw-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shimDir);
        try
        {
            WriteShim(shimDir, "sudo", "exec \"$@\"\n");
            WriteShim(shimDir, "udevadm", "exit 0\n");
            WriteShim(shimDir, "usermod", "exit 0\n");
            WriteShim(shimDir, "id", "printf '%s\\n' users\n");
            WriteShim(shimDir, "chown", "exit 0\n");

            var cmd = InputAccessSetupHelper.ManualInstallCommand();
            // ReSharper disable once UseObjectOrCollectionInitializer -- Environment["PATH"] is set post-construction, matching CommandRunner's convention.
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            // Shim dir first so sudo/udevadm/usermod resolve to our stubs, then real
            // coreutils (head/cat) from the standard bin dirs.
            psi.Environment["PATH"] =
                $"{shimDir}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin";
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.StandardInput.Write(cmd);
            proc.StandardInput.Close();
            proc.WaitForExit();
            return proc.ExitCode;
        }
        finally
        {
            try
            {
                Directory.Delete(shimDir, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static void WriteShim(string dir, string name, string body)
    {
        var path = Path.Join(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
#pragma warning restore CA1416
    }

    // --- Privileged install ownership gating -----------------------------
    // These tests execute the exact shell script piped to pkexec against a temp
    // tree (udevadm stubbed), exercising the root-side check-and-write semantics
    // rather than merely pinning script text.

    [Fact]
    public async Task InstallAsync_refuses_a_foreign_rule_without_changing_its_bytes()
    {
        using var env = new SysConfEnvironment();
        const string foreignContent =
            "# Managed by another application\nSUBSYSTEM==\"input\", MODE=\"0600\"\n";
        SysConfEnvironment.WriteRule(foreignContent);
        var originalBytes = File.ReadAllBytes(InputAccessSetupHelper.UdevRulePath);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(originalBytes, File.ReadAllBytes(InputAccessSetupHelper.UdevRulePath));
        Assert.Contains(InputAccessSetupHelper.UdevRulePath, result.Message);
        Assert.Contains("move or rename", result.Detail);
    }

    [Fact]
    public async Task InstallAsync_refuses_a_rule_whose_header_only_shares_the_marker_prefix()
    {
        using var env = new SysConfEnvironment();
        const string foreignContent = "# Installed by TypeWhisperer\nforeign rule\n";
        SysConfEnvironment.WriteRule(foreignContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignContent, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task InstallAsync_refuses_an_existing_non_regular_target()
    {
        using var env = new SysConfEnvironment();
        Directory.CreateDirectory(InputAccessSetupHelper.UdevRulePath);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.True(Directory.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task InstallAsync_refuses_to_write_through_a_symlink()
    {
        using var env = new SysConfEnvironment();
        var linkPath = InputAccessSetupHelper.UdevRulePath;
        var targetPath = Path.Join(env.Dir, "foreign-target.rules");
        const string targetContent = "# Foreign symlink target\n";
        File.WriteAllText(targetPath, targetContent);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.CreateSymbolicLink(linkPath, targetPath);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleSymlinkExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(targetContent, File.ReadAllText(targetPath));
        Assert.Equal(targetPath, new FileInfo(linkPath).LinkTarget);
        Assert.Contains("symbolic link", result.Message);
    }

    [Fact]
    public async Task InstallAsync_refuses_a_customized_marker_owned_rule()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(
            "# Installed by TypeWhisper — old header\nold rule content\n"
        );
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(
            "# Installed by TypeWhisper — old header\nold rule content\n",
            File.ReadAllText(InputAccessSetupHelper.UdevRulePath)
        );
    }

    [Fact]
    public async Task InstallAsync_writes_an_absent_rule_with_marker_and_RemoveAsync_removes_it()
    {
        using var env = new SysConfEnvironment();
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var install = await helper.InstallAsync(CancellationToken.None);

        Assert.True(install.Success);
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
        Assert.StartsWith(
            "# Installed by TypeWhisper",
            File.ReadAllText(InputAccessSetupHelper.UdevRulePath)
        );

        var remove = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(
            remove.Success,
            $"{remove.Message}\n{remove.Detail}\n{runner.LastPrivilegedResult?.StandardError}"
        );
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
        Assert.False(File.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task InstallAsync_without_pkexec_returns_the_manual_command()
    {
        using var env = new SysConfEnvironment();
        // PATH points only at the temp dir and pkexec is NOT placed there.

        var runner = new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(runner.Invocations, i => i.FileName == "pkexec");
        Assert.Equal(InputAccessSetupHelper.ManualInstallCommand(), result.Detail);
    }

    [Fact]
    public async Task InstallAsync_passes_a_bounded_timeout_and_recovers_when_it_fires()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");

        var runner = new FakeProcessRunner
        {
            // Model a stalled polkit prompt that outlives the timeout window.
            Default = new ProcessRunResult(true, true, -1, string.Empty, string.Empty),
        };
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        // Falls back to the manual command so the wizard isn't wedged.
        Assert.Equal(InputAccessSetupHelper.ManualInstallCommand(), result.Detail);
        // A bounded timeout was actually requested on the privileged call.
        var call = Assert.Single(runner.Invocations);
        Assert.NotNull(call.Timeout);
        Assert.True(call.Timeout <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task InstallAsync_flags_a_cancelled_auth_prompt()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");

        var runner = new FakeProcessRunner();
        // pkexec exits 126 when the polkit dialog is dismissed.
        runner.SetExitCode((file, _) => file == "pkexec", 126);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task InputGroupFallback_records_provenance_only_when_membership_was_absent()
    {
        using var env = new SysConfEnvironment();
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(alreadyMember: false, usermodLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.AddToInputGroupFallbackAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.GroupMembershipAdded);
        Assert.Contains("-aG input -- typewhisper-test", File.ReadAllText(usermodLog));
        Assert.Equal(
            "state=owned\nuid=4242\nusername=typewhisper-test",
            File.ReadAllText(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath)
        );
    }

    [Fact]
    public async Task InputGroupFallback_definitive_add_failure_clears_pending_provenance()
    {
        using var env = new SysConfEnvironment();
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: false,
            usermodLog,
            usermodExitCode: 9,
            memberAfterUsermodFailure: false
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.AddToInputGroupFallbackAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(9, runner.LastPrivilegedResult?.ExitCode);
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task InputGroupFallback_ambiguous_add_failure_retains_pending_provenance()
    {
        using var env = new SysConfEnvironment();
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: false,
            usermodLog,
            usermodExitCode: 9,
            memberAfterUsermodFailure: true
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.AddToInputGroupFallbackAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(9, runner.LastPrivilegedResult?.ExitCode);
        Assert.Equal(
            "state=pending-add\nuid=4242\nusername=typewhisper-test",
            File.ReadAllText(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath)
        );
    }

    [Fact]
    public async Task InputGroupFallback_does_not_claim_preexisting_membership()
    {
        using var env = new SysConfEnvironment();
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(alreadyMember: true, usermodLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.AddToInputGroupFallbackAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.GroupMembershipAdded);
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
        Assert.False(File.Exists(usermodLog));
    }

    [Fact]
    public async Task InputGroupFallback_succeeds_even_when_a_foreign_rule_occupies_the_rule_path()
    {
        using var env = new SysConfEnvironment();
        const string foreignRule = "# Managed by another application\n";
        SysConfEnvironment.WriteRule(foreignRule);
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(alreadyMember: false, usermodLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.AddToInputGroupFallbackAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.GroupMembershipAdded);
        Assert.Contains("-aG input -- typewhisper-test", File.ReadAllText(usermodLog));
        Assert.Equal(
            "state=owned\nuid=4242\nusername=typewhisper-test",
            File.ReadAllText(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath)
        );
        Assert.Equal(foreignRule, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_deletes_a_TypeWhisper_owned_rule_and_retriggers_udev()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(InputAccessSetupHelper.UdevRuleContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
        Assert.False(File.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_with_matching_provenance_uses_gpasswd_and_requires_relogin()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(InputAccessSetupHelper.UdevRuleContent);
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequiresRelogin);
        Assert.True(File.Exists(gpasswdLog));
        Assert.Contains("-d typewhisper-test input", File.ReadAllText(gpasswdLog));
        Assert.False(File.Exists(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_without_gpasswd_falls_back_to_usermod_rG()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            installGpasswd: false
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequiresRelogin);
        Assert.Contains("-rG input -- typewhisper-test", File.ReadAllText(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_without_provenance_never_removes_preexisting_membership()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(InputAccessSetupHelper.UdevRuleContent);
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(alreadyMember: true, usermodLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RequiresRelogin);
        Assert.False(File.Exists(usermodLog));
    }

    [Fact]
    public async Task RemoveAsync_with_rule_absent_but_provenance_present_still_revokes_group()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequiresRelogin);
        Assert.Contains("-d typewhisper-test input", File.ReadAllText(gpasswdLog));
        Assert.False(File.Exists(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_group_removal_failure_preserves_provenance()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog,
            gpasswdExitCode: 9
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("-d typewhisper-test input", File.ReadAllText(gpasswdLog));
        Assert.False(File.Exists(usermodLog));
        Assert.True(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_recovers_pending_add_provenance()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("pending-add");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequiresRelogin);
        Assert.Contains("-d typewhisper-test input", File.ReadAllText(gpasswdLog));
        Assert.False(File.Exists(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_clears_pending_add_when_membership_was_not_applied()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("pending-add");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var runner = env.CreateGroupScriptRunner(alreadyMember: false, usermodLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RequiresRelogin);
        Assert.False(File.Exists(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
    }

    [Fact]
    public async Task RemoveAsync_without_pkexec_offers_only_a_provenance_guarded_group_revoke()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var helper = new InputAccessSetupHelper(new FakeProcessRunner());

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.RequiresRelogin);
        Assert.Contains("gpasswd -d", result.Detail);
        Assert.Contains("usermod -rG input", result.Detail);
        Assert.Contains("state=owned", result.Detail);
        Assert.StartsWith("sudo sh -c", result.Detail!);
        Assert.False(result.Detail!.StartsWith("sudo usermod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveAsync_without_pkexec_offers_independent_rule_and_group_commands()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(InputAccessSetupHelper.UdevRuleContent);
        env.WriteGroupProvenance("owned");
        var helper = new InputAccessSetupHelper(new FakeProcessRunner());

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        var detail = Assert.IsType<string>(result.Detail);
        var secondCommand = detail.IndexOf("\nsudo sh -c ", StringComparison.Ordinal);
        Assert.True(secondCommand > 0);
        var ruleCommand = detail[..secondCommand];
        var groupCommand = detail[(secondCommand + 1)..];
        Assert.StartsWith("sudo sh -c ", ruleCommand);
        Assert.Contains("udevadm control --reload", ruleCommand);
        Assert.DoesNotContain("gpasswd -d", ruleCommand);
        Assert.StartsWith("sudo sh -c ", groupCommand);
        Assert.Contains("gpasswd -d", groupCommand);
        Assert.Contains("state=owned", groupCommand);
        Assert.DoesNotContain("udevadm control --reload", groupCommand);
    }

    [Fact]
    public async Task RemoveAsync_group_only_manual_fallback_survives_identity_failure()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var identityCalls = 0;
        InputAccessSetupHelper.CurrentIdentityOverride = () =>
            ++identityCalls == 1 ? (4242u, "typewhisper-test") : (4242u, "bad\nname");
        var helper = new InputAccessSetupHelper(new FakeProcessRunner());

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Manual input-group revocation is unavailable", result.Detail);
    }

    [Fact]
    public async Task RemoveAsync_refuses_group_revoke_when_provenance_was_swapped_before_the_privileged_run()
    {
        using var env = new SysConfEnvironment();
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        const string foreignRecord = "state=owned\nuid=4242\nusername=someone-else";
        var runner = new SwapProvenanceBeforePrivilegedRunner(
            env.CreateGroupScriptRunner(alreadyMember: true, usermodLog),
            foreignRecord
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.InputGroupGrantUnsafeExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.False(File.Exists(usermodLog));
        Assert.Equal(
            foreignRecord,
            File.ReadAllText(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath)
        );
    }

    [Fact]
    public async Task RemoveAsync_with_edited_owned_rule_and_provenance_still_revokes_group()
    {
        using var env = new SysConfEnvironment();
        var editedRule = InputAccessSetupHelper.UdevRuleContent + "# administrator edit\n";
        SysConfEnvironment.WriteRule(editedRule);
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Refused);
        Assert.True(result.RequiresRelogin);
        Assert.True(result.GroupRevocationCompleted);
        Assert.Null(result.GroupRevocationFailure);
        Assert.DoesNotContain("provenance record was cleared", result.Detail);
        Assert.Contains("-d typewhisper-test input", File.ReadAllText(gpasswdLog));
        Assert.False(File.Exists(usermodLog));
        Assert.False(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
        Assert.Equal(editedRule, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_with_edited_owned_rule_reports_independent_group_failure()
    {
        using var env = new SysConfEnvironment();
        var editedRule = InputAccessSetupHelper.UdevRuleContent + "# administrator edit\n";
        SysConfEnvironment.WriteRule(editedRule);
        env.WriteGroupProvenance("owned");
        var usermodLog = Path.Join(env.Dir, "usermod.log");
        var gpasswdLog = Path.Join(env.Dir, "gpasswd.log");
        var runner = env.CreateGroupScriptRunner(
            alreadyMember: true,
            usermodLog,
            gpasswdLog: gpasswdLog,
            gpasswdExitCode: 9
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Refused);
        Assert.False(result.GroupRevocationCompleted);
        Assert.Contains("gpasswd", result.GroupRevocationFailure);
        Assert.True(File.Exists(InputAccessSetupHelper.CurrentInputGroupGrantRecordPath));
        Assert.Equal(editedRule, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_refuses_when_a_foreign_file_replaced_ours_before_the_delete()
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(InputAccessSetupHelper.UdevRuleContent);
        var runner = new SwapBeforePrivilegedRunner(
            env,
            "# Managed by another application\nSUBSYSTEM==\"input\", MODE=\"0660\"\n"
        );
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(
            "# Managed by another application\nSUBSYSTEM==\"input\", MODE=\"0660\"\n",
            File.ReadAllText(InputAccessSetupHelper.UdevRulePath)
        );
    }

    [Fact]
    public async Task RemoveAsync_root_transaction_refuses_a_foreign_rule()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");
        // A rule a user / distro package wrote at our conventional path — no marker.
        SysConfEnvironment.WriteRule("# Some other tool's rule\nSUBSYSTEM==\"input\", MODE=\"0660\"\n");

        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.True(File.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_root_transaction_refuses_mid_body_marker_mention()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");
        // Ownership is anchored to the first line; a mid-body marker mention
        // must not count.
        SysConfEnvironment.WriteRule(
            "# Managed by another application\n# note: not Installed by TypeWhisper\nSUBSYSTEM==\"input\", MODE=\"0660\"\n"
        );

        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            InputAccessSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.True(File.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_with_nothing_on_disk_but_pkexec_available_still_reactivates_udev()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");
        var udevadmLog = Path.Join(env.Dir, "udevadm.log");

        var runner = env.CreatePrivilegedScriptRunner(udevadmLog);
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
        var invocations = File.ReadAllText(udevadmLog);
        Assert.Contains("control --reload", invocations);
        Assert.Contains("trigger --subsystem-match=input --action=change", invocations);
    }

    /// <summary>
    ///     Redirects the helper's /etc lookups into a temp dir and restricts PATH
    ///     to a temp dir so the test can never reach the real pkexec/udevadm, while
    ///     <c>DesktopDetector.BinaryExists</c> still resolves fake binaries placed
    ///     there. Mirrors <c>YdotoolSetupHelperTests.TempEnvironment</c>.
    /// </summary>
    private sealed class SysConfEnvironment : IDisposable
    {
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _originalSysConf = InputAccessSetupHelper.SysConfDirOverride;
        private readonly string? _originalRootManagedState =
            InputAccessSetupHelper.RootManagedArtifactStateRootOverride;
        private readonly string? _originalGrantState =
            InputAccessSetupHelper.InputGroupGrantStateDirectoryOverride;
        private readonly Func<(uint Uid, string UserName)>? _originalIdentity =
            InputAccessSetupHelper.CurrentIdentityOverride;
        private readonly string[]? _originalSeatManagerPaths =
            InputAccessSetupHelper.SeatManagerDirectoryPathsOverride;
        private readonly string _pathDir = Path.Join(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");

        public SysConfEnvironment()
        {
            Dir = Path.Join(Path.GetTempPath(), $"tw-etc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_pathDir);
            Directory.CreateDirectory(Dir);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
            InputAccessSetupHelper.SysConfDirOverride = Dir;
            InputAccessSetupHelper.RootManagedArtifactStateRootOverride = Path.Join(
                Dir,
                "managed-root-state"
            );
            InputAccessSetupHelper.InputGroupGrantStateDirectoryOverride = Path.Join(
                Dir,
                "input-group-grants"
            );
            InputAccessSetupHelper.CurrentIdentityOverride = () => (4242, "typewhisper-test");
            Directory.CreateDirectory(
                Path.GetDirectoryName(InputAccessSetupHelper.UdevRulePath)!
            );
        }

        public string Dir { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            InputAccessSetupHelper.SysConfDirOverride = _originalSysConf;
            InputAccessSetupHelper.RootManagedArtifactStateRootOverride =
                _originalRootManagedState;
            InputAccessSetupHelper.InputGroupGrantStateDirectoryOverride =
                _originalGrantState;
            InputAccessSetupHelper.CurrentIdentityOverride = _originalIdentity;
            InputAccessSetupHelper.SeatManagerDirectoryPathsOverride =
                _originalSeatManagerPaths;
            TryDelete(_pathDir);
            TryDelete(Dir);
        }

        public void PutFakeBinaryOnPath(string name)
        {
            File.WriteAllText(Path.Join(_pathDir, name), "#!/bin/sh\n");
        }

        public PrivilegedScriptRunner CreatePrivilegedScriptRunner(string? udevadmLog = null)
        {
            PutFakeBinaryOnPath("pkexec");
            if (udevadmLog is null)
            {
                PutExecutableSuccessBinaryOnPath("udevadm");
            }
            else
            {
                Assert.DoesNotContain('\'', udevadmLog);
                WriteShim(
                    _pathDir,
                    "udevadm",
                    $"printf '%s\\n' \"$*\" >> '{udevadmLog}'\nexit 0\n"
                );
            }

            PutExecutableSuccessBinaryOnPath("chown");
            return new PrivilegedScriptRunner(
                $"{_pathDir}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin"
            );
        }

        public PrivilegedScriptRunner CreateGroupScriptRunner(
            bool alreadyMember,
            string usermodLog,
            int usermodExitCode = 0,
            string? gpasswdLog = null,
            int gpasswdExitCode = 0,
            bool installGpasswd = true,
            bool? memberAfterUsermodFailure = null
        )
        {
            _ = CreatePrivilegedScriptRunner();
            string idBody;
            if (memberAfterUsermodFailure is null)
            {
                idBody = alreadyMember
                    ? "printf '%s\\n' 'users input'\n"
                    : "printf '%s\\n' users\n";
            }
            else
            {
                Assert.False(alreadyMember);
                var firstLookupMarker = Path.Join(Dir, "id-first-lookup");
                Assert.DoesNotContain('\'', firstLookupMarker);
                idBody =
                    $"if [ -e '{firstLookupMarker}' ]; then\n"
                    + (memberAfterUsermodFailure.Value
                        ? "  printf '%s\\n' 'users input'\n"
                        : "  printf '%s\\n' users\n")
                    + $"else\n  : > '{firstLookupMarker}'\n  printf '%s\\n' users\nfi\n";
            }

            WriteShim(
                _pathDir,
                "id",
                idBody
            );
            Assert.DoesNotContain('\'', usermodLog);
            WriteShim(
                _pathDir,
                "usermod",
                $"printf '%s\\n' \"$*\" >> '{usermodLog}'\nexit {usermodExitCode}\n"
            );
            if (installGpasswd)
            {
                gpasswdLog ??= Path.Join(Dir, "gpasswd.log");
                Assert.DoesNotContain('\'', gpasswdLog);
                WriteShim(
                    _pathDir,
                    "gpasswd",
                    $"printf '%s\\n' \"$*\" >> '{gpasswdLog}'\nexit {gpasswdExitCode}\n"
                );
                return new PrivilegedScriptRunner(
                    $"{_pathDir}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin"
                );
            }

            foreach (var command in new[] { "flock", "mkdir", "chmod", "cat", "rm" })
            {
                File.CreateSymbolicLink(
                    Path.Join(_pathDir, command),
                    Path.Join("/usr/bin", command)
                );
            }

            return new PrivilegedScriptRunner(_pathDir);
        }

        public void ForceNoSeatManager()
        {
            InputAccessSetupHelper.SeatManagerDirectoryPathsOverride =
            [
                Path.Join(Dir, "missing-systemd-seats"),
                Path.Join(Dir, "missing-elogind-seats"),
            ];
        }

        public void WriteGroupProvenance(string state)
        {
            Directory.CreateDirectory(InputAccessSetupHelper.InputGroupGrantStateDirectory);
            File.WriteAllText(
                InputAccessSetupHelper.CurrentInputGroupGrantRecordPath,
                $"state={state}\nuid=4242\nusername=typewhisper-test"
            );
        }

        public static void WriteRule(string content)
        {
            var path = InputAccessSetupHelper.UdevRulePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private void PutExecutableSuccessBinaryOnPath(string name)
        {
            File.CreateSymbolicLink(Path.Join(_pathDir, name), "/bin/true");
        }

        private static void TryDelete(string dir)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private sealed class PrivilegedScriptRunner(string commandPath) : IProcessRunner
    {
        private readonly ProcessRunner _processRunner = new();

        public ProcessRunResult? LastPrivilegedResult { get; private set; }

        public async Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            if (fileName != "pkexec")
            {
                return new ProcessRunResult(true, false, 0, string.Empty, string.Empty);
            }

            var result = await _processRunner.RunAsync(
                "/bin/sh",
                [],
                new Dictionary<string, string> { ["PATH"] = commandPath },
                standardInput,
                timeout,
                ct: ct
            );
            LastPrivilegedResult = result;
            return result;
        }
    }

    // Simulates the same stale-check race for the group-grant provenance record:
    // the record matched at probe time, but a foreign record replaces it before the
    // privileged script runs, so the root-side managed-state guard must refuse the
    // group-removal command rather than trust the earlier probe.
    private sealed class SwapProvenanceBeforePrivilegedRunner(
        PrivilegedScriptRunner inner,
        string foreignContent
    ) : IProcessRunner
    {
        public ProcessRunResult? LastPrivilegedResult => inner.LastPrivilegedResult;

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            if (fileName == "pkexec")
            {
                File.WriteAllText(
                    InputAccessSetupHelper.CurrentInputGroupGrantRecordPath,
                    foreignContent
                );
            }

            return inner.RunAsync(fileName, args, environment, standardInput, timeout, ct: ct);
        }
    }

    // Simulates the stale-check race: overwrites the rule file with foreign content
    // right before the privileged script runs, then delegates to the real script so
    // the root-side ownership re-validation is exercised against the swapped file.
    private sealed class SwapBeforePrivilegedRunner : IProcessRunner
    {
        private readonly PrivilegedScriptRunner _inner;
        private readonly string _foreignContent;

        public SwapBeforePrivilegedRunner(SysConfEnvironment env, string foreignContent)
        {
            _inner = env.CreatePrivilegedScriptRunner();
            _foreignContent = foreignContent;
        }

        public ProcessRunResult? LastPrivilegedResult => _inner.LastPrivilegedResult;

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            if (fileName == "pkexec")
            {
                File.WriteAllText(InputAccessSetupHelper.UdevRulePath, _foreignContent);
            }

            return _inner.RunAsync(fileName, args, environment, standardInput, timeout, ct: ct);
        }
    }
}
