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
        var content = InputAccessSetupHelper.UdevRuleContent;

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

    [Fact]
    public async Task InstallAsync_pipes_a_pkexec_heredoc_that_writes_reloads_triggers_and_settles()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");

        var runner = new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.InstallAsync(CancellationToken.None);

        Assert.True(result.Success);
        // InstallAsync makes exactly one privileged call.
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("pkexec", call.FileName);
        Assert.Contains("/bin/sh", call.Args);
        // The privileged script content goes over stdin.
        var script = call.StandardInput;
        Assert.NotNull(script);
        Assert.Contains($"cat > {InputAccessSetupHelper.UdevRulePath} <<'EOF'", script);
        Assert.Contains("udevadm control --reload", script);
        Assert.Contains("udevadm trigger --subsystem-match=input --action=change", script);
        Assert.Contains("udevadm settle", script);
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
            Default = new ProcessRunResult(true, true, -1, string.Empty, string.Empty)
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
        env.PutFakeBinaryOnPath("pkexec");
        env.WriteRule(InputAccessSetupHelper.UdevRuleContent);

        var runner = new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(
            runner.Invocations,
            i => i.FileName == "pkexec" && i.Args.Contains("/bin/sh")
        );
        Assert.Contains("udevadm trigger", runner.LastStandardInput);
        Assert.Contains($"rm -f {InputAccessSetupHelper.UdevRulePath}", runner.LastStandardInput);
    }

    [Fact]
    public async Task RemoveAsync_leaves_a_foreign_rule_untouched()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");
        // A rule a user / distro package wrote at our conventional path — no marker.
        env.WriteRule("# Some other tool's rule\nSUBSYSTEM==\"input\", MODE=\"0660\"\n");

        var runner = new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        // Reported as success-with-caveat; the foreign file is never deleted.
        Assert.True(result.Success);
        Assert.DoesNotContain(runner.Invocations, i => i.FileName == "pkexec");
        Assert.True(File.Exists(InputAccessSetupHelper.UdevRulePath));
    }

    [Fact]
    public async Task RemoveAsync_is_a_noop_when_no_rule_exists()
    {
        using var env = new SysConfEnvironment();
        env.PutFakeBinaryOnPath("pkexec");

        var runner = new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(runner.Invocations, i => i.FileName == "pkexec");
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
        private readonly string _pathDir = Path.Join(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");

        public SysConfEnvironment()
        {
            Dir = Path.Join(Path.GetTempPath(), $"tw-etc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_pathDir);
            Directory.CreateDirectory(Dir);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
            InputAccessSetupHelper.SysConfDirOverride = Dir;
        }

        public string Dir { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            InputAccessSetupHelper.SysConfDirOverride = _originalSysConf;
            TryDelete(_pathDir);
            TryDelete(Dir);
        }

        public void PutFakeBinaryOnPath(string name)
        {
            File.WriteAllText(Path.Join(_pathDir, name), "#!/bin/sh\n");
        }

        public void WriteRule(string content)
        {
            var path = InputAccessSetupHelper.UdevRulePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
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
}
