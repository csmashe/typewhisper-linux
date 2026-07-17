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
        // The write is fronted by a privileged guard block, so pasting it never
        // follows a symlink or truncates foreign config at our path.
        Assert.StartsWith("sudo sh -c", state.CopyCommand!);
        Assert.Contains(InputAccessSetupHelper.UdevRulePath, state.CopyCommand!);
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

    [Fact]
    public async Task EvaluateAsync_when_opted_out_and_no_rule_installed_is_satisfied_with_no_action()
    {
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            ruleInstalled: () => false,
            evdevOptedIn: () => false
        );

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyOptedOut"], state.Summary);
        Assert.Null(state.ActionLabel);
    }

    [Fact]
    public async Task EvaluateAsync_when_opted_out_and_rule_still_installed_offers_a_revoke_action_without_blocking_finish()
    {
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            ruleInstalled: () => true,
            evdevOptedIn: () => false
        );

        var state = await task.EvaluateAsync(CancellationToken.None);

        Assert.Equal(SetupTaskStatusKind.Satisfied, state.Kind);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyOptedOutRuleInstalled"], state.Summary);
        Assert.Equal(
            Loc.Instance["Setup.GlobalHotkeyOptedOutRuleInstalledDetail"],
            state.Detail
        );
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyRevokeButton"], state.ActionLabel);
    }

    // --- RunActionAsync ---------------------------------------------------

    [Fact]
    public async Task RunAction_when_opted_out_and_no_rule_installed_is_a_noop()
    {
        var runner = new FakeProcessRunner();
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            ruleInstalled: () => false,
            runner: runner,
            evdevOptedIn: () => false
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(Loc.Instance["Setup.GlobalHotkeyOptedOut"], outcome.Message);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task RunAction_when_opted_out_but_rule_still_installed_revokes_it_via_the_helper()
    {
        using var env = new PkexecOnPath();
        PkexecOnPath.WriteOwnedRule();
        var runner = new FakeProcessRunner();
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            ruleInstalled: () => true,
            runner: runner,
            evdevOptedIn: () => false
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains(
            runner.Invocations,
            i => i.FileName == "pkexec" && i.Args.Contains("/bin/sh")
        );
        Assert.Contains(runner.Invocations, i => i.StandardInput?.Contains("rm -f") == true);
    }

    [Fact]
    public async Task RunAction_never_falls_through_to_install_when_opted_out_even_if_access_is_missing()
    {
        var runner = new FakeProcessRunner();
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            seatManagerPresent: () => false,
            ruleInstalled: () => false,
            runner: runner,
            evdevOptedIn: () => false
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(runner.Invocations);
    }

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
    public async Task RunAction_with_seat_manager_and_successful_reprobe_succeeds_without_group_fallback()
    {
        using var env = new PkexecOnPath();
        var runner = new FakeProcessRunner();
        var granted = false;

        // Access is false at the guard, true after the rule install + udev trigger.
        var task = Build(
            isWayland: true,
            hasAccess: Seq(false, true),
            seatManagerPresent: () => true,
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
    public async Task RunAction_without_seat_manager_falls_back_to_input_group_when_access_still_denied()
    {
        using var env = new PkexecOnPath();
        var runner = new FakeProcessRunner();
        var granted = false;

        // No logind: access stays false even after the rule install, so the task
        // falls back to the group join + relogin path.
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            seatManagerPresent: () => false,
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
    public async Task RunAction_with_seat_manager_and_failed_reprobe_does_not_use_group_fallback()
    {
        using var env = new PkexecOnPath();
        var runner = new FakeProcessRunner();
        var granted = false;
        var task = Build(
            isWayland: true,
            hasAccess: () => false,
            seatManagerPresent: () => true,
            runner: runner,
            onGranted: _ =>
            {
                granted = true;
                return Task.CompletedTask;
            }
        );

        var outcome = await task.RunActionAsync(CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(Loc.Instance["Shortcuts.KeyboardAccessNotConfirmed"], outcome.Message);
        Assert.Equal(Loc.Instance["Shortcuts.KeyboardAccessNotConfirmedDetail"], outcome.Detail);
        Assert.Contains(runner.Invocations, i => i.FileName == "pkexec" && i.Args.Contains("/bin/sh"));
        Assert.DoesNotContain(runner.Invocations, i => i.Args.Contains("usermod"));
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
        Func<bool>? seatManagerPresent = null,
        Func<bool>? listedInGroup = null,
        Func<bool>? ruleInstalled = null,
        IProcessRunner? runner = null,
        Func<CancellationToken, Task>? onGranted = null,
        Func<bool>? evdevOptedIn = null
    )
    {
        runner ??= new FakeProcessRunner();
        var helper = new InputAccessSetupHelper(runner);
        return new GlobalHotkeySetupTask(
            () => isWayland,
            runner,
            helper,
            hasAccess ?? (() => false),
            seatManagerPresent ?? (() => true),
            listedInGroup ?? (() => false),
            ruleInstalled ?? (() => false),
            onGranted ?? (_ => Task.CompletedTask),
            evdevOptedIn ?? (() => true)
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
        private readonly string _pathDir = Path.Join(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");
        private readonly string _sysConfDir = Path.Join(Path.GetTempPath(), $"tw-etc-{Guid.NewGuid():N}");

        public PkexecOnPath(bool installPkexec = true)
        {
            Directory.CreateDirectory(_pathDir);
            Directory.CreateDirectory(_sysConfDir);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
            InputAccessSetupHelper.SysConfDirOverride = _sysConfDir;
            if (installPkexec)
            {
                File.WriteAllText(Path.Join(_pathDir, "pkexec"), "#!/bin/sh\n");
            }
        }

        public static void WriteOwnedRule()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InputAccessSetupHelper.UdevRulePath)!);
            File.WriteAllText(
                InputAccessSetupHelper.UdevRulePath,
                InputAccessSetupHelper.UdevRuleContent
            );
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
