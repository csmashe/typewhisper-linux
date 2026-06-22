using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Setup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     State-machine tests for <see cref="GlobalHotkeySetupTask" /> after the
///     switch from input-group gating to keyboard-access gating. The task's
///     environment probes (session type, keyboard openability, group-file
///     membership, backend hot-swap) are injected through the internal test
///     constructor so each branch is exercised deterministically. The privileged
///     install runs through the <see cref="FakeProcessRunner" /> seam.
/// </summary>
public sealed class GlobalHotkeySetupTaskTests
{
    // --- EvaluateAsync ----------------------------------------------------

    [Fact]
    public async Task X11_session_is_satisfied_with_no_action()
    {
        var task = Build(isWayland: false, hasAccess: () => false);

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyActiveX11"], state.Summary);
    }

    [Fact]
    public async Task Wayland_with_keyboard_access_is_satisfied_via_evdev()
    {
        var task = Build(isWayland: true, hasAccess: () => true);

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyActiveEvdev"], state.Summary);
    }

    [Fact]
    public async Task Wayland_without_access_needs_action_to_install_the_rule()
    {
        var task = Build(isWayland: true, hasAccess: () => false, listedInGroup: () => false);

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.NeedsAction, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyNeedsInputGroup"], state.Summary);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAddMeButton"], state.ActionLabel);
        // The copyable fallback leads with the uaccess rule install, not an
        // `input`-group join. (A non-logind usermod fallback follows, but guarded
        // so logind users pasting the block never actually join the group.)
        Assert.Equal(InputAccessSetupHelper.ManualInstallCommand(), state.CopyCommand);
        Assert.StartsWith("sudo tee", state.CopyCommand!);
        Assert.Contains("uaccess", state.CopyCommand!);
    }

    [Fact]
    public async Task NeedsAction_copy_does_not_lead_with_join_the_input_group()
    {
        // Regression guard: the action summary/label must not tell the user to
        // join the input group — that's the old, reboot-requiring flow.
        var task = Build(isWayland: true, hasAccess: () => false, listedInGroup: () => false);

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.DoesNotContain("input group", state.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("input group", state.ActionLabel!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wayland_with_pending_relogin_after_rule_install_is_satisfied_with_caveat()
    {
        // Non-logind: we installed the rule AND fell back to the group, so only a
        // re-login remains — satisfied-with-caveat so Finish isn't blocked.
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            listedInGroup: () => true,
            ruleInstalled: () => true
        );

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAddedRelogin"], state.Summary);
    }

    [Fact]
    public async Task Wayland_with_stale_group_membership_but_no_rule_offers_the_rule()
    {
        // Upgrade case: the user ran the OLD usermod flow (listed in the group) but
        // our rule isn't installed yet and the session can't open keyboards. Rather
        // than telling them to log out, offer the rule — it can grant access now.
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            listedInGroup: () => true,
            ruleInstalled: () => false
        );

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.NeedsAction, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAddMeButton"], state.ActionLabel);
    }

    // --- RunActionAsync ---------------------------------------------------

    [Fact]
    public async Task RunAction_when_already_accessible_reports_already_active()
    {
        var runner = new FakeProcessRunner();
        var task = Build(isWayland: true, hasAccess: () => true, runner: runner);

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAlreadyActive"], outcome.Message);
        // No privileged work when access already exists.
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task RunAction_installs_rule_then_satisfies_in_session_without_reboot()
    {
        using var env = new PkexecOnPath();
        var runner = new FakeProcessRunner();
        var granted = false;

        // Access is false at the guard, true after the rule install + udev trigger.
        var task = Build(
            isWayland: true,
            hasAccess: Seq(false, true),
            runner: runner,
            onGranted: _ =>
            {
                granted = true;
                return Task.CompletedTask;
            }
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAccessEnabled"], outcome.Message);
        // Backend was hot-swapped so the now-readable devices attach with no restart.
        Assert.True(granted);
        // The rule was installed via pkexec; the input-group fallback was NOT used.
        Assert.Contains(runner.Invocations, i => i.FileName == "pkexec" && i.Args.Contains("/bin/sh"));
        Assert.DoesNotContain(runner.Invocations, i => i.Args.Contains("usermod"));
    }

    [Fact]
    public async Task RunAction_falls_back_to_input_group_when_access_still_denied()
    {
        using var env = new PkexecOnPath();
        var runner = new FakeProcessRunner();
        var granted = false;

        // No logind: access stays false even after the rule install, so the task
        // falls back to the group join + relogin path.
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            runner: runner,
            onGranted: _ =>
            {
                granted = true;
                return Task.CompletedTask;
            }
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyAddedToGroup"], outcome.Message);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyReloginToActivate"], outcome.Detail);
        Assert.Contains(runner.Invocations, i => i.FileName == "pkexec" && i.Args.Contains("usermod"));
        // Access never became available, so the backend is not hot-swapped.
        Assert.False(granted);
    }

    [Fact]
    public async Task RunAction_without_pkexec_offers_the_manual_command()
    {
        // PATH restricted to an empty temp dir → pkexec is unreachable.
        using var env = new PkexecOnPath(installPkexec: false);
        var runner = new FakeProcessRunner();
        var task = Build(isWayland: true, hasAccess: () => false, runner: runner);

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(Loc.Instance["Setup.PkexecUnavailable"], outcome.Message);
        Assert.Empty(runner.Invocations);
    }

    // --- helpers ----------------------------------------------------------

    private static GlobalHotkeySetupTask Build(
        bool isWayland = true,
        Func<bool>? hasAccess = null,
        Func<bool>? listedInGroup = null,
        Func<bool>? ruleInstalled = null,
        IProcessRunner? runner = null,
        Func<CancellationToken, Task>? onGranted = null
    )
    {
        runner ??= new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);
        return new GlobalHotkeySetupTask(
            () => isWayland,
            runner,
            helper,
            hasAccess ?? (() => false),
            listedInGroup ?? (() => false),
            ruleInstalled ?? (() => false),
            onGranted ?? (_ => Task.CompletedTask)
        );
    }

    /// <summary>Returns each value in turn, then sticks on the last one.</summary>
    private static Func<bool> Seq(params bool[] values)
    {
        var i = 0;
        return () => values[Math.Min(i++, values.Length - 1)];
    }

    /// <summary>
    ///     Restricts PATH to a temp dir (optionally containing a fake pkexec) so
    ///     <c>DesktopDetector.BinaryExists("pkexec")</c> resolves deterministically,
    ///     and redirects the udev-rule path into a temp /etc. Restores both on dispose.
    /// </summary>
    private sealed class PkexecOnPath : IDisposable
    {
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _originalSysConf = InputAccessSetupHelper.SysConfDirOverride;
        private readonly string _pathDir = Path.Combine(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");
        private readonly string _sysConfDir = Path.Combine(Path.GetTempPath(), $"tw-etc-{Guid.NewGuid():N}");

        public PkexecOnPath(bool installPkexec = true)
        {
            Directory.CreateDirectory(_pathDir);
            Directory.CreateDirectory(_sysConfDir);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
            InputAccessSetupHelper.SysConfDirOverride = _sysConfDir;
            if (installPkexec)
            {
                File.WriteAllText(Path.Combine(_pathDir, "pkexec"), "#!/bin/sh\n");
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            InputAccessSetupHelper.SysConfDirOverride = _originalSysConf;
            TryDelete(_pathDir);
            TryDelete(_sysConfDir);
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
