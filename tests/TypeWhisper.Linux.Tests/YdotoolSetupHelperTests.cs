// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read/WriteAllText is deliberate in these test assertions; the async overload would only add await noise with no benefit off the hot path.
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Insertion;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the pure / <c>internal static</c> surface and process orchestration
///     of <see cref="YdotoolSetupHelper" /> through its runner seam. The libc
///     <c>access</c> probe remains host-coupled; privileged script behavior is
///     exercised against redirected temp paths.
/// </summary>
public sealed class YdotoolSetupHelperTests
{
    [Fact]
    public void UserUnitFilePath_honors_XDG_CONFIG_HOME_when_set()
    {
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var custom = Path.Join(Path.GetTempPath(), $"tw-xdg-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", custom);

            var path = YdotoolSetupHelper.UserUnitFilePath();

            Assert.Equal(Path.Join(custom, "systemd", "user", "ydotoold.service"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void UserUnitFilePath_falls_back_to_dot_config_when_XDG_unset()
    {
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

            var path = YdotoolSetupHelper.UserUnitFilePath();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(
                Path.Join(home, ".config", "systemd", "user", "ydotoold.service"),
                path
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void BuildUserUnitContent_first_line_carries_ownership_marker()
    {
        var content = YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold");

        var firstLine = content.Split('\n')[0];
        Assert.StartsWith("# Installed by TypeWhisper", firstLine);
    }

    [Fact]
    public void BuildUserUnitContent_embeds_exact_ExecStart_path()
    {
        const string path = "/opt/custom/bin/ydotoold";

        var content = YdotoolSetupHelper.BuildUserUnitContent(path);

        Assert.Contains($"ExecStart={path}", content);
        Assert.Contains("WantedBy=default.target", content);
        Assert.Contains("Restart=on-failure", content);
    }

    [Fact]
    public void ResolveBinaryPath_finds_binary_on_PATH()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        var dir = Path.Join(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var fake = Path.Join(dir, "fake-binary");
            File.WriteAllText(fake, "#!/bin/sh\n");

            Environment.SetEnvironmentVariable("PATH", dir);

            Assert.Equal(fake, YdotoolSetupHelper.ResolveBinaryPath("fake-binary"));
            Assert.Null(YdotoolSetupHelper.ResolveBinaryPath("definitely-not-here"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void IsFileOwnedByTypeWhisper_detects_marker()
    {
        var withMarker = Path.GetTempFileName();
        var withoutMarker = Path.GetTempFileName();
        try
        {
            File.WriteAllText(withMarker, "# Installed by TypeWhisper\nsome content\n");
            File.WriteAllText(withoutMarker, "# Some other tool wrote this\n");
            // Only a first-line header (bare marker or "marker — ...") counts as
            // ours; mid-body, negated, and prefix-collision mentions are foreign.
            var negatedMarker = Path.GetTempFileName();
            var midBodyMarker = Path.GetTempFileName();
            var prefixCollision = Path.GetTempFileName();
            var realHeader = Path.GetTempFileName();
            File.WriteAllText(negatedMarker, "# This is not Installed by TypeWhisper\nloop\n");
            File.WriteAllText(midBodyMarker, "# Foreign\n# Installed by TypeWhisper\n");
            File.WriteAllText(prefixCollision, "# Installed by TypeWhisperer\nloop\n");
            File.WriteAllText(realHeader, "# Installed by TypeWhisper — old header\nx\n");

            Assert.True(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(withMarker));
            Assert.True(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(realHeader));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(withoutMarker));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(negatedMarker));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(midBodyMarker));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(prefixCollision));
            File.Delete(negatedMarker);
            File.Delete(midBodyMarker);
            File.Delete(prefixCollision);
            File.Delete(realHeader);
            Assert.False(
                YdotoolSetupHelper.IsFileOwnedByTypeWhisper(
                    Path.Join(Path.GetTempPath(), $"tw-missing-{Guid.NewGuid():N}")
                )
            );
        }
        finally
        {
            try
            {
                File.Delete(withMarker);
            }
            catch
            {
                // best effort
            }

            try
            {
                File.Delete(withoutMarker);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void BuildUserUnitContent_round_trips_through_ownership_check()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));

            Assert.True(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }
    }

    // --- Privileged install ownership gating -----------------------------
    // These tests execute the exact shell script piped to pkexec, but point
    // the helper's system-config paths at a temp tree and replace udevadm /
    // modprobe with harmless test executables. This exercises the root-side
    // check-and-write semantics rather than merely pinning script text.

    [Fact]
    public async Task InstallUdevRuleAsync_refuses_a_foreign_modules_load_file()
    {
        using var env = new TempEnvironment();
        const string foreignContent = "# Managed by the distribution\nloop\n";
        env.WriteModulesLoad(foreignContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.ModulesLoadConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignContent, File.ReadAllText(env.ModulesLoadPath));
        Assert.False(File.Exists(env.UdevRulePath));
        Assert.Contains(env.ModulesLoadPath, result.Message);
        Assert.Contains("move or rename", result.Detail);
    }

    [Fact]
    public async Task InstallUdevRuleAsync_refuses_a_foreign_udev_rule()
    {
        using var env = new TempEnvironment();
        const string foreignModulesContent = "# Managed by the distribution\nuinput\n";
        const string foreignRuleContent = "# Managed by another application\nKERNEL==\"fuse\"\n";
        env.WriteModulesLoad(foreignModulesContent);
        env.WriteUdevRule(foreignRuleContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignModulesContent, File.ReadAllText(env.ModulesLoadPath));
        Assert.Equal(foreignRuleContent, File.ReadAllText(env.UdevRulePath));
        Assert.Contains(env.UdevRulePath, result.Message);
        Assert.Contains("move or rename", result.Detail);
    }

    [Fact]
    public async Task InstallUdevRuleAsync_rewrites_marker_owned_files()
    {
        using var env = new TempEnvironment();
        // Use the real production header ("# Installed by TypeWhisper — ..."),
        // not a bare marker line — a bare marker would pass even under a
        // whole-line-only match that rejects every real owned file.
        env.WriteModulesLoad("# Installed by TypeWhisper — old header\nold modules content\n");
        env.WriteUdevRule("# Installed by TypeWhisper — old header\nold rule content\n");
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, runner.LastPrivilegedResult?.ExitCode);
        var modulesContent = File.ReadAllText(env.ModulesLoadPath);
        Assert.StartsWith("# Installed by TypeWhisper", modulesContent);
        Assert.Contains("\nuinput\n", modulesContent);
        Assert.DoesNotContain("old modules content", modulesContent);
        var ruleContent = File.ReadAllText(env.UdevRulePath);
        Assert.StartsWith("# Installed by TypeWhisper", ruleContent);
        Assert.Contains("KERNEL==\"uinput\"", ruleContent);
        Assert.DoesNotContain("old rule content", ruleContent);
    }

    [Fact]
    public async Task InstallUdevRuleAsync_preserves_foreign_files_that_already_achieve_the_goal()
    {
        using var env = new TempEnvironment();
        // A bare `uinput` line (whitespace-padded) genuinely loads the module;
        // the inline-comment form that doesn't is covered by a conflict test below.
        const string foreignModulesContent = "# Managed by the distribution\n  uinput  \n";
        const string foreignRuleContent =
            "# Managed by the distribution\n"
            + "KERNEL==\"uinput\", TAG+=\"uaccess\", GROUP=\"input\", MODE=\"0660\", OPTIONS+=\"static_node=uinput\"\n";
        env.WriteModulesLoad(foreignModulesContent);
        env.WriteUdevRule(foreignRuleContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(foreignModulesContent, File.ReadAllText(env.ModulesLoadPath));
        Assert.Equal(foreignRuleContent, File.ReadAllText(env.UdevRulePath));
    }

    [Fact]
    public async Task InstallUdevRuleAsync_udev_conflict_leaves_no_new_modules_load_file()
    {
        using var env = new TempEnvironment();
        // modules-load target is absent, udev rule is foreign. Both targets are
        // validated before either write, so the refusal must leave NO
        // modules-load.d entry behind loading uinput on every boot.
        const string foreignRuleContent = "# Managed by another application\nKERNEL==\"fuse\"\n";
        env.WriteUdevRule(foreignRuleContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.UdevRuleConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.False(File.Exists(env.ModulesLoadPath));
        Assert.Equal(foreignRuleContent, File.ReadAllText(env.UdevRulePath));
    }

    [Fact]
    public async Task InstallUdevRuleAsync_refuses_a_file_that_only_negates_the_marker()
    {
        using var env = new TempEnvironment();
        // Neither carries our first-line header nor already loads uinput — a
        // genuine conflict; the file must be preserved, not truncated.
        const string foreignContent =
            "# Managed by the distribution\n"
            + "# This is not Installed by TypeWhisper\n"
            + "loop\n";
        env.WriteModulesLoad(foreignContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.ModulesLoadConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignContent, File.ReadAllText(env.ModulesLoadPath));
        Assert.False(File.Exists(env.UdevRulePath));
    }

    [Fact]
    public async Task InstallUdevRuleAsync_refuses_a_uinput_line_with_an_inline_comment()
    {
        using var env = new TempEnvironment();
        // modules-load.d does not strip inline comments: this parses as a module
        // named "uinput # boot" that never loads, so it's a conflict, not goal-achieving.
        const string foreignContent = "# Managed by the distribution\nuinput # boot\n";
        env.WriteModulesLoad(foreignContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.ModulesLoadConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignContent, File.ReadAllText(env.ModulesLoadPath));
    }

    [Fact]
    public async Task InstallUdevRuleAsync_refuses_a_file_whose_header_only_shares_the_marker_prefix()
    {
        using var env = new TempEnvironment();
        const string foreignContent = "# Installed by TypeWhisperer\nloop\n";
        env.WriteModulesLoad(foreignContent);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            YdotoolSetupHelper.ModulesLoadConflictExitCode,
            runner.LastPrivilegedResult?.ExitCode
        );
        Assert.Equal(foreignContent, File.ReadAllText(env.ModulesLoadPath));
    }

    [Theory]
    [InlineData(true, YdotoolSetupHelper.ModulesLoadSymlinkExitCode)]
    [InlineData(false, YdotoolSetupHelper.UdevRuleSymlinkExitCode)]
    public async Task InstallUdevRuleAsync_refuses_to_write_through_symlinks(
        bool modulesLoadSymlink,
        int expectedExitCode
    )
    {
        using var env = new TempEnvironment();
        var linkPath = modulesLoadSymlink ? env.ModulesLoadPath : env.UdevRulePath;
        var targetPath = Path.Join(env.SysConfDir, "foreign-target.conf");
        const string targetContent = "# Foreign symlink target\n";
        File.WriteAllText(targetPath, targetContent);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.CreateSymbolicLink(linkPath, targetPath);
        var runner = env.CreatePrivilegedScriptRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.InstallUdevRuleAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedExitCode, runner.LastPrivilegedResult?.ExitCode);
        Assert.Equal(targetContent, File.ReadAllText(targetPath));
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        Assert.Contains("symbolic link", result.Message);
    }

    // --- RemoveAsync ownership gating -------------------------------------
    // RemoveAsync runs `systemctl`, so it's only testable through the
    // IProcessRunner seam. The regression these guard: SetUpAsync respects a
    // pre-existing foreign ydotoold user unit but also `enable --now's it, so
    // an unconditional `disable --now` on remove would kill a service the
    // user relies on, persistently, past logout.

    [Fact]
    public async Task RemoveAsync_leaves_a_foreign_ydotoold_user_unit_enabled()
    {
        using var env = new TempEnvironment();
        // A ydotoold user unit the user (or a distro/AUR package) wrote —
        // no TypeWhisper ownership marker.
        TempEnvironment.WriteUserUnit(
            "# Some other tool's ydotoold unit\n[Service]\nExecStart=/usr/bin/ydotoold\n"
        );
        // systemctl on PATH so the disable action isn't skipped for the *wrong*
        // reason — this proves the ownership gate, not a missing binary.
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            runner.Invocations,
            i => i.FileName == "systemctl" && i.Args.Contains("disable")
        );
        // The foreign unit file is left in place, untouched.
        Assert.True(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    [Fact]
    public async Task RemoveAsync_disables_and_deletes_a_TypeWhisper_owned_user_unit()
    {
        using var env = new TempEnvironment();
        TempEnvironment.WriteUserUnit(YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(
            runner.Invocations,
            i =>
                i.FileName == "systemctl"
                && i.Args.Contains("disable")
                && i.Args.Contains("ydotoold.service")
        );
        Assert.False(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    [Fact]
    public async Task RemoveAsync_keeps_the_unit_file_when_disable_fails()
    {
        using var env = new TempEnvironment();
        TempEnvironment.WriteUserUnit(YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        runner.FailWhen(
            (file, args) => file == "systemctl" && args.Contains("disable"),
            "Failed to disable unit: Connection refused"
        );
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        // Fail closed: removal reports failure and leaves the unit file in
        // place so the user can retry — deleting it now would risk a
        // dangling enablement symlink.
        Assert.False(result.Success);
        Assert.True(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    /// <summary>
    ///     Points XDG_CONFIG_HOME and PATH at throwaway temp dirs for one test,
    ///     then restores them. PATH is deliberately restricted to the temp dir so
    ///     the test can never reach the real systemctl/pkexec — process execution
    ///     goes through the injected <see cref="FakeProcessRunner" />, while
    ///     <c>DesktopDetector.BinaryExists</c> still resolves whatever fake
    ///     binaries the test places there.
    /// </summary>
    private sealed class TempEnvironment : IDisposable
    {
        private readonly string _configHome = Path.Join(
            Path.GetTempPath(),
            $"tw-cfg-{Guid.NewGuid():N}"
        );

        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

        private readonly string? _originalXdg = Environment.GetEnvironmentVariable(
            "XDG_CONFIG_HOME"
        );

        private readonly string _pathDir = Path.Join(
            Path.GetTempPath(),
            $"tw-path-{Guid.NewGuid():N}"
        );

        // Redirect the helper's hardcoded /etc lookups (udev rule, modules-load
        // entry) into a temp dir so the tests don't read the host's real /etc —
        // a machine with TypeWhisper's ydotool setup already installed there
        // would otherwise make RemoveAsync fail-close on a file it can't delete.
        // Uses the internal test-only override (not an env var) so the override
        // never reaches the privileged pkexec scripts in production.
        private readonly string? _originalSysConf = YdotoolSetupHelper.SysConfDirOverride;

        private readonly string _sysConfDir = Path.Join(
            Path.GetTempPath(),
            $"tw-etc-{Guid.NewGuid():N}"
        );

        public string ModulesLoadPath =>
            Path.Join(_sysConfDir, "modules-load.d", "uinput.conf");

        // ReSharper disable once ConvertToAutoPropertyWhenPossible -- the backing field is referenced directly in six other places (path builders, ctor, override, dispose); routing all of them through the property adds indirection for no gain.
        public string SysConfDir => _sysConfDir;

        public string UdevRulePath =>
            Path.Join(_sysConfDir, "udev", "rules.d", "60-ydotool.rules");

        public TempEnvironment()
        {
            Directory.CreateDirectory(_pathDir);
            Directory.CreateDirectory(_sysConfDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ModulesLoadPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(UdevRulePath)!);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
            YdotoolSetupHelper.SysConfDirOverride = _sysConfDir;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdg);
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            YdotoolSetupHelper.SysConfDirOverride = _originalSysConf;
            try
            {
                Directory.Delete(_configHome, true);
            }
            catch
            {
                /* best effort */
            }

            try
            {
                Directory.Delete(_pathDir, true);
            }
            catch
            {
                /* best effort */
            }

            try
            {
                Directory.Delete(_sysConfDir, true);
            }
            catch
            {
                /* best effort */
            }
        }

        public static void WriteUserUnit(string content)
        {
            var path = YdotoolSetupHelper.UserUnitFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void PutFakeBinaryOnPath(string name)
        {
            File.WriteAllText(Path.Join(_pathDir, name), "#!/bin/sh\n");
        }

        public PrivilegedScriptRunner CreatePrivilegedScriptRunner()
        {
            PutFakeBinaryOnPath("pkexec");
            PutExecutableSuccessBinaryOnPath("udevadm");
            PutExecutableSuccessBinaryOnPath("modprobe");
            return new PrivilegedScriptRunner(
                $"{_pathDir}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin"
            );
        }

        public void WriteModulesLoad(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModulesLoadPath)!);
            File.WriteAllText(ModulesLoadPath, content);
        }

        public void WriteUdevRule(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UdevRulePath)!);
            File.WriteAllText(UdevRulePath, content);
        }

        private void PutExecutableSuccessBinaryOnPath(string name)
        {
            var path = Path.Join(_pathDir, name);
            File.CreateSymbolicLink(path, "/bin/true");
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
}
