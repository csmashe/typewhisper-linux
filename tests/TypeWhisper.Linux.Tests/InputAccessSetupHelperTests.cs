// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read/WriteAllText is deliberate in these test assertions; the async overload would only add await noise with no benefit off the hot path.
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
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
    [Theory]
    [InlineData("# Managed by another application\nfoo\n", false)] // foreign header
    [InlineData("# Installed by TypeWhisperer\nfoo\n", false)] // prefix-only, not ours
    [InlineData("# Installed by TypeWhisper — old header\nold\n", false)] // customized-owned
    public void ManualInstallCommand_write_block_refuses_foreign_targets(
        string existing,
        bool shouldRewrite
    )
    {
        using var env = new SysConfEnvironment();
        SysConfEnvironment.WriteRule(existing);

        var exit = RunManualWriteBlock();

        if (shouldRewrite)
        {
            Assert.Equal(0, exit);
            Assert.Equal(
                InputAccessSetupHelper.UdevRuleContent,
                File.ReadAllText(InputAccessSetupHelper.UdevRulePath)
            );
        }
        else
        {
            Assert.NotEqual(0, exit);
            Assert.Equal(existing, File.ReadAllText(InputAccessSetupHelper.UdevRulePath));
        }
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

        Assert.True(remove.Success);
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
    public async Task RemoveAsync_is_a_noop_when_no_rule_exists()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");

        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
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
            TryDelete(_pathDir);
            TryDelete(Dir);
        }

        public void PutFakeBinaryOnPath(string name)
        {
            File.WriteAllText(Path.Join(_pathDir, name), "#!/bin/sh\n");
        }

        public PrivilegedScriptRunner CreatePrivilegedScriptRunner()
        {
            PutFakeBinaryOnPath("pkexec");
            PutExecutableSuccessBinaryOnPath("udevadm");
            PutExecutableSuccessBinaryOnPath("chown");
            return new PrivilegedScriptRunner(
                $"{_pathDir}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin"
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
