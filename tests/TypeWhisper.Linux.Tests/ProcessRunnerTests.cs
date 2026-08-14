using System.Diagnostics;
using System.Security.Cryptography;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.ProcessTestChild;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(5);

    // Upper bound on a detached drain: it must abandon near the 250 ms grace, not wait the full
    // timeout. Held between that grace and the 2 s timeout the descendant test uses so a regression
    // back to full-timeout draining trips it.
    private static readonly TimeSpan s_maxPostExitDrain = TimeSpan.FromSeconds(1);

    // Mirrors ProcessRunner's private post-exit drain grace (250 ms).
    private static readonly TimeSpan s_postExitGrace = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task RunAsync_with_detachAfterExit_abandons_promptly_when_descendant_holds_stdout_open()
    {
        var pidFile = NewPidFile();
        int? childProcessId = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                [
                    "-c",
                    "sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; exit 0",
                    "process-runner-test",
                    pidFile,
                ],
                timeout: TimeSpan.FromSeconds(2),
                detachAfterExit: true
            );
            childProcessId = await WaitForProcessIdAsync(pidFile);

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < s_maxPostExitDrain,
                "detachAfterExit waited the full timeout draining a descendant-held pipe instead of "
                + $"abandoning after the post-exit grace; elapsed {stopwatch.Elapsed}."
            );
            Assert.True(result.Succeeded);
            Assert.True(result.Started);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.True(
                ProcessExists(childProcessId.Value),
                "A successful bounded drain unexpectedly killed the pipe-holding descendant."
            );
        }
        finally
        {
            if (childProcessId is { } leakedProcessId)
            {
                TryKillProcess(leakedProcessId);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_by_default_preserves_parent_output_behind_a_short_lived_descendant()
    {
        // Parent prints then exits, but a descendant holds the stdout pipe open past the 250 ms
        // grace yet within the timeout. Without detachAfterExit the run must keep draining until the
        // pipe closes, so the buffered parent output is captured rather than discarded.
        var stopwatch = Stopwatch.StartNew();
        var runTask = new ProcessRunner().RunAsync(
            "/bin/bash",
            ["-c", "printf '%s' parent-output; sleep 0.7 & exit 0"],
            timeout: TimeSpan.FromSeconds(5)
        );

        var result = await runTask.WaitAsync(s_testGuard);
        stopwatch.Stop();

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("parent-output", result.StandardOutput);
        // The descendant held the pipe ~700 ms; if the default had short-drained at the grace it
        // would have abandoned the read (and lost the output) well before that.
        Assert.True(
            stopwatch.Elapsed > s_postExitGrace,
            $"The default drain abandoned before the descendant released the pipe; elapsed {stopwatch.Elapsed}."
        );
    }

    [Fact]
    public async Task RunAsync_with_detachAfterExit_abandons_promptly_even_without_a_timeout()
    {
        // detachAfterExit must honor the short grace independently of a lifecycle timeout: with no
        // timeout the run would otherwise wait for EOF forever behind the daemon-held pipe.
        var pidFile = NewPidFile();
        int? childProcessId = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                [
                    "-c",
                    "sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; exit 0",
                    "process-runner-test",
                    pidFile,
                ],
                detachAfterExit: true
            );
            childProcessId = await WaitForProcessIdAsync(pidFile);

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < s_maxPostExitDrain,
                "detachAfterExit ignored its grace without a timeout and waited on the descendant; "
                + $"elapsed {stopwatch.Elapsed}."
            );
            Assert.True(result.Succeeded);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            if (childProcessId is { } leakedProcessId)
            {
                TryKillProcess(leakedProcessId);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_captures_all_output_from_fast_process()
    {
        var runTask = new ProcessRunner().RunAsync(
            "/bin/bash",
            ["-c", "echo hello"],
            timeout: TimeSpan.FromSeconds(2)
        );

        var result = await runTask.WaitAsync(s_testGuard);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task RunAsync_kills_process_when_exit_wait_times_out()
    {
        var pidFile = NewPidFile();
        FakeProcessIds? processIds = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
                timeout: TimeSpan.FromSeconds(1)
            );
            processIds = await WaitForProcessIdsAsync(pidFile);

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < s_testGuard);
            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_times_out_when_child_does_not_read_standard_input()
    {
        var pidFile = NewPidFile();
        FakeProcessIds? processIds = null;
        try
        {
            var standardInput = new string('x', 256 * 1024);
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
                standardInput: standardInput,
                timeout: TimeSpan.FromSeconds(2)
            );
            processIds = await WaitForProcessIdsAsync(pidFile);

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < s_testGuard,
                $"ProcessRunner did not bound the stdin write; elapsed {stopwatch.Elapsed}."
            );
            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_does_not_launch_when_caller_token_is_already_canceled()
    {
        var pidFile = NewPidFile();
        var argumentsWereRead = false;
        var arguments = new ObservedArguments(
            ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
            () => argumentsWereRead = true
        );
        using var cts = new CancellationTokenSource();
        // ReSharper disable once MethodHasAsyncOverload -- the token must be canceled before RunAsync is invoked.
        cts.Cancel();
        try
        {
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                arguments,
                timeout: TimeSpan.FromSeconds(30),
                ct: cts.Token
            );

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
            Assert.False(argumentsWereRead, "A pre-canceled run progressed toward process launch.");
            Assert.False(File.Exists(pidFile), "A pre-canceled run launched the fake child.");
        }
        finally
        {
            if (TryReadProcessIds(pidFile, out var leakedProcessIds))
            {
                TryKillProcesses(leakedProcessIds);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_kills_and_rethrows_on_caller_cancellation()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        FakeProcessIds? processIds = null;
        try
        {
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
                timeout: TimeSpan.FromSeconds(30),
                ct: cts.Token
            );
            processIds = await WaitForProcessIdsAsync(pidFile);
            Assert.False(runTask.IsCompleted);

            // ReSharper disable once MethodHasAsyncOverload -- the test deliberately triggers synchronous CTS cancellation to exercise ProcessRunner's cancel-and-kill path.
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here; no async continuation to await in the finally guard.
            cts.Cancel();
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_cancels_blocked_standard_input_and_kills_process_tree()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        FakeProcessIds? processIds = null;
        try
        {
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
                standardInput: new string('x', 256 * 1024),
                ct: cts.Token
            );
            processIds = await WaitForProcessIdsAsync(pidFile);
            Assert.False(runTask.IsCompleted);

            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation establishes the exact stdin-write phase under test.
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_caller_cancellation_wins_when_racing_private_timeout()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        FakeProcessIds? processIds = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", PublishPidPairAndWaitScript(), "process-runner-test", pidFile],
                timeout: TimeSpan.FromSeconds(1),
                ct: cts.Token
            );
            processIds = await WaitForProcessIdsAsync(pidFile);

            // Well clear of the 1s private timeout: on a loaded machine an 800ms
            // target left too little margin and the timeout could win the race.
            var callerDeadline = TimeSpan.FromMilliseconds(300) - stopwatch.Elapsed;
            if (callerDeadline > TimeSpan.Zero)
            {
                cts.CancelAfter(callerDeadline);
            }
            else
            {
                // ReSharper disable once MethodHasAsyncOverload -- the deadline already elapsed.
                cts.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_caller_cancellation_wins_during_post_exit_output_drain()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        FakeProcessIds? processIds = null;
        try
        {
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                [
                    "-c",
                    "sleep 30 & child=$!; printf '%s %s' \"$$\" \"$child\" > \"$1\"; exit 0",
                    "process-runner-test",
                    pidFile,
                ],
                timeout: TimeSpan.FromSeconds(30),
                ct: cts.Token
            );
            processIds = await WaitForProcessIdsAsync(pidFile);
            await AssertProcessDisappearsAsync(processIds.Value.BashProcessId);
            Assert.True(ProcessExists(processIds.Value.ChildProcessId));
            Assert.False(runTask.IsCompleted);

            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation establishes the output-drain phase under test.
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
        }
        finally
        {
            // Without process-group semantics, the child is unreachable from the exited
            // root, so the test reaps it itself rather than relying on ProcessRunner.
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            TryKillProcesses(processIds);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_handles_simultaneous_input_and_output_pipe_pressure()
    {
        var input = Enumerable.Range(0, 2 * 1024 * 1024)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        var result = await new ProcessRunner()
            .RunOneShotAsync(
                ChildCommand("pressure", (256 * 1024).ToString()),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(5),
                    StandardInput: new BinaryProcessInput(input),
                    StandardOutput: ProcessCaptureMode.Binary,
                    StandardError: ProcessCaptureMode.Binary
                )
            )
            .WaitAsync(s_testGuard);

        Assert.True(result.Succeeded);
        Assert.Equal(256 * 1024, result.StandardError.Length);
        var report = System.Text.Encoding.UTF8.GetBytes(
            $"{input.Length}:{Convert.ToHexString(SHA256.HashData(input))}\n"
        );
        Assert.True(result.StandardOutput.AsSpan().EndsWith(report));
        Assert.Equal(256 * 1024 + report.Length, result.StandardOutput.Length);
    }

    [Fact]
    public async Task RunOneShotAsync_private_timeout_kills_and_reaps_the_leader()
    {
        var pidFile = NewPidFile();
        int? processId = null;
        try
        {
            var run = new ProcessRunner().RunOneShotAsync(
                ChildCommand("wait", pidFile),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromMilliseconds(500))
            );
            processId = await WaitForProcessIdAsync(pidFile);

            var result = await run.WaitAsync(s_testGuard);

            Assert.Equal(ProcessRunStatus.TimedOut, result.Status);
            Assert.Null(result.ExitCode);
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            if (processId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_unexpected_post_start_failure_reaps_and_propagates()
    {
        var pidFile = NewPidFile();
        using var callerCts = new CancellationTokenSource();
        int? processId = null;
        try
        {
            var run = new ProcessRunner().RunOneShotAsync(
                ChildCommand("wait", pidFile),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromMilliseconds(500),
                    StandardInput: new UnsupportedProcessInput()
                ),
                callerCts.Token
            );
            processId = await WaitForProcessIdAsync(pidFile);

            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await run.WaitAsync(s_testGuard, CancellationToken.None)
            );

            Assert.Equal("input", exception.ParamName);
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            if (processId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_rejects_invalid_delays_before_running_the_child()
    {
        var marker = NewPidFile();
        var runner = new ProcessRunner();
        var command = new ProcessCommand(
            "/bin/bash",
            ["-c", "printf ran > \"$1\"", "process-runner-test", marker]
        );

        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => runner.RunOneShotAsync(
                    command,
                    new ProcessOneShotOptions(Timeout: TimeSpan.FromMilliseconds(-2))
                )
            );
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => runner.RunOneShotAsync(
                    command,
                    new ProcessOneShotOptions(
                        PostExitPipePolicy: ProcessPostExitPipePolicy.AbandonAfterGrace,
                        PostExitDrainGrace: TimeSpan.FromMilliseconds(-2)
                    )
                )
            );

            // The side-effecting command must never have run.
            Assert.False(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_broken_stdin_still_enforces_the_deadline_and_reaps()
    {
        var pidFile = NewPidFile();
        int? processId = null;
        try
        {
            var run = new ProcessRunner().RunOneShotAsync(
                new ProcessCommand(
                    "/bin/bash",
                    [
                        "-c",
                        CloseStdinAndWaitScript(),
                        "process-runner-test",
                        pidFile,
                    ]
                ),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromMilliseconds(500),
                    StandardInput: new Utf8ProcessInput(new string('x', 512 * 1024))
                )
            );
            processId = await WaitForProcessIdAsync(pidFile);

            var result = await run.WaitAsync(s_testGuard);

            Assert.Equal(ProcessRunStatus.TimedOut, result.Status);
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            if (processId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Session_completion_is_terminal_when_a_descendant_holds_the_pipes()
    {
        var pidFile = NewPidFile();
        int? childId = null;
        try
        {
            var start = new ProcessRunner().StartSession(
                ChildCommand("hold-pipes-after-exit", pidFile, "10000"),
                new ProcessSessionOptions(
                    StandardOutput: ProcessSessionOutputMode.Lines,
                    StandardError: ProcessSessionOutputMode.Lines
                )
            );
            Assert.True(start.Started);
            await using var session = start.Session!;
            childId = await WaitForProcessIdAsync(pidFile);

            var exit = await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(ProcessExitReason.Exited, exit.Reason);
            Assert.Equal(0, exit.ExitCode);
            Assert.True(ProcessExists(childId.Value));
        }
        finally
        {
            if (childId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_caller_cancellation_throws_after_tree_cleanup()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        FakeProcessIds? processIds = null;
        try
        {
            var run = new ProcessRunner().RunOneShotAsync(
                ChildCommand("spawn-child", pidFile),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(30)),
                cts.Token
            );
            processIds = await WaitForColonProcessIdsAsync(pidFile);
            // ReSharper disable once MethodHasAsyncOverload -- the test deliberately triggers synchronous CTS cancellation to exercise ProcessRunner's cancel-and-kill path.
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await run.WaitAsync(s_testGuard)
            );
            await AssertProcessesDisappearAsync(processIds.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            TryKillProcesses(processIds);
            File.Delete(pidFile);
            File.Delete($"{pidFile}.child");
        }
    }

    [Fact]
    public async Task RunOneShotAsync_caller_cancellation_wins_timeout_race()
    {
        var pidFile = NewPidFile();
        using var cts = new CancellationTokenSource();
        int? processId = null;
        try
        {
            var run = new ProcessRunner().RunOneShotAsync(
                ChildCommand("wait", pidFile),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(2)),
                cts.Token
            );
            processId = await WaitForProcessIdAsync(pidFile);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await run.WaitAsync(s_testGuard)
            );
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            if (processId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_waits_for_delayed_exit_and_captures_exit_code()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await new ProcessRunner().RunOneShotAsync(
            ChildCommand("delay-exit", "300", "17"),
            new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(3))
        );

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250));
        Assert.Equal(ProcessRunStatus.Exited, result.Status);
        Assert.Equal(17, result.ExitCode);
    }

    [Fact]
    public async Task RunOneShotAsync_drains_both_streams_in_discard_mode()
    {
        var result = await new ProcessRunner().RunOneShotAsync(
            ChildCommand("flood", (1024 * 1024).ToString()),
            new ProcessOneShotOptions(
                Timeout: TimeSpan.FromSeconds(5),
                StandardOutput: ProcessCaptureMode.Discard,
                StandardError: ProcessCaptureMode.Discard
            )
        );

        Assert.True(result.Succeeded);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(ProcessOutputStatus.Complete, result.OutputStatus);
    }

    [Fact]
    public async Task RunOneShotAsync_reports_abandoned_inherited_pipes_after_exit()
    {
        var pidFile = NewPidFile();
        int? childId = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await new ProcessRunner().RunOneShotAsync(
                ChildCommand("hold-pipes-after-exit", pidFile, "3000"),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(5),
                    PostExitPipePolicy: ProcessPostExitPipePolicy.AbandonAfterGrace,
                    PostExitDrainGrace: TimeSpan.FromMilliseconds(150)
                )
            );
            childId = await WaitForProcessIdAsync(pidFile);

            // Under the grandchild's 3 s pipe hold, so a regression to full draining still
            // trips, with margin for dotnet startup and CI scheduling noise.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"The abandon path drained the inherited pipes; elapsed {stopwatch.Elapsed}."
            );
            Assert.True(result.Succeeded);
            Assert.Equal(
                ProcessOutputStatus.AbandonedAfterExit,
                result.OutputStatus
            );
            Assert.True(ProcessExists(childId.Value));
        }
        finally
        {
            if (childId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Session_natural_and_terminated_completion_are_single_and_drained()
    {
        var runner = new ProcessRunner();
        var naturalStart = runner.StartSession(
            ChildCommand("monitor-lines", "3", "20"),
            new ProcessSessionOptions(
                StandardOutput: ProcessSessionOutputMode.Lines,
                StandardError: ProcessSessionOutputMode.Lines
            )
        );
        Assert.True(naturalStart.Started);
        await using var natural = naturalStart.Session!;
        var linesTask = ReadAllLinesAsync(natural);
        var naturalCompletion = natural.Completion;

        var naturalResult = await naturalCompletion.WaitAsync(s_testGuard);
        var lines = await linesTask;

        Assert.Same(naturalCompletion, natural.Completion);
        Assert.Equal(ProcessExitReason.Exited, naturalResult.Reason);
        Assert.Equal(0, naturalResult.ExitCode);
        Assert.Equal(6, lines.Count);
        Assert.Contains(lines, line => line.Stream == ProcessStream.StandardOutput);
        Assert.Contains(lines, line => line.Stream == ProcessStream.StandardError);

        var pidFile = NewPidFile();
        try
        {
            var terminatedStart = runner.StartSession(
                ChildCommand("wait", pidFile),
                new ProcessSessionOptions()
            );
            Assert.True(terminatedStart.Started);
            await using var terminated = terminatedStart.Session!;
            await WaitForProcessIdAsync(pidFile);
            var completion = terminated.Completion;

            terminated.Terminate();
            terminated.Terminate();
            var terminatedResult = await completion.WaitAsync(s_testGuard);

            Assert.Same(completion, terminated.Completion);
            Assert.Equal(ProcessExitReason.Terminated, terminatedResult.Reason);
            Assert.False(terminated.IsRunning);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task LaunchDetached_returns_promptly_and_child_continues()
    {
        var marker = NewPidFile();
        int? processId = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ProcessRunner().LaunchDetached(
                ChildCommand("detached-marker", marker, "1500")
            );

            Assert.True(result.Started, result.StartError);
            // Half the child's delay, so a runner that waited for it can't pass while
            // dotnet startup on a loaded machine still has room.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
                $"LaunchDetached did not return promptly; elapsed {stopwatch.Elapsed}."
            );
            var contents = await WaitForFileContentsAsync(
                marker,
                static text => SplitMarker(text) is [_, "continued"] parts
                               && int.TryParse(parts[0], out _)
            );
            var parts = contents.Split(':');
            processId = int.Parse(parts[0]);
            Assert.Equal("continued", parts[1]);
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            if (processId is { } leaked)
            {
                TryKillProcess(leaked);
            }

            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RunOneShotAsync_start_failure_is_typed()
    {
        var result = await new ProcessRunner().RunOneShotAsync(
            new ProcessCommand(
                $"/definitely/missing/typewhisper-{Guid.NewGuid():N}",
                []
            ),
            new ProcessOneShotOptions()
        );

        Assert.Equal(ProcessRunStatus.StartFailed, result.Status);
        Assert.Null(result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StartError));
    }

    private static string NewPidFile()
    {
        return Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-{Guid.NewGuid():N}.pid"
        );
    }

    private static ProcessCommand ChildCommand(params string[] arguments)
    {
        return new ProcessCommand(
            "dotnet",
            [typeof(ProcessTestChildMarker).Assembly.Location, .. arguments]
        );
    }

    private static async Task<List<ProcessOutputLine>> ReadAllLinesAsync(
        IPluginProcessSession session
    )
    {
        var lines = new List<ProcessOutputLine>();
        await foreach (var line in session.ReadOutputAsync())
        {
            lines.Add(line);
        }

        return lines;
    }

    // isComplete guards against reading a marker the child is still writing: "1234:" parses
    // as a truncated child id, so keep polling until the whole expected shape is there.
    private static async Task<string> WaitForFileContentsAsync(
        string path,
        Func<string, bool> isComplete
    )
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < s_testGuard)
        {
            try
            {
                if (File.Exists(path))
                {
                    var contents = await File.ReadAllTextAsync(path);
                    if (!string.IsNullOrWhiteSpace(contents) && isComplete(contents))
                    {
                        return contents;
                    }
                }
            }
            catch (IOException)
            {
                // The child may still be publishing the file.
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("Child did not publish the marker in time.");
    }

    private static async Task<FakeProcessIds> WaitForColonProcessIdsAsync(string path)
    {
        var contents = await WaitForFileContentsAsync(
            path,
            static text => SplitMarker(text) is [_, _] parts
                           && int.TryParse(parts[0], out _)
                           && int.TryParse(parts[1], out _)
        );
        var parts = contents.Split(':');
        return new FakeProcessIds(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string[] SplitMarker(string contents)
    {
        return contents.Split(':', StringSplitOptions.RemoveEmptyEntries);
    }

    // Closes the read end of the stdin pipe and stays alive, so the supervisor's pending
    // write fails with EPIPE while the child still owes us an exit.
    private static string CloseStdinAndWaitScript()
    {
        return "exec 0<&-; printf '%s' \"$$\" > \"$1\"; sleep 30";
    }

    private static string PublishPidPairAndWaitScript()
    {
        return "sleep 30 & child=$!; printf '%s %s' \"$$\" \"$child\" > \"$1\"; wait \"$child\"";
    }

    private static async Task<int> WaitForProcessIdAsync(string pidFile)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < s_testGuard)
        {
            try
            {
                if (File.Exists(pidFile))
                {
                    var contents = await File.ReadAllTextAsync(pidFile);
                    if (int.TryParse(contents, out var processId) && processId > 0)
                    {
                        return processId;
                    }
                }
            }
            catch (IOException)
            {
                // The child may still be publishing the file; retry briefly.
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("Fake child did not publish its PID in time.");
    }

    private static async Task<FakeProcessIds> WaitForProcessIdsAsync(string pidFile)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < s_testGuard)
        {
            try
            {
                if (TryReadProcessIds(pidFile, out var processIds))
                {
                    return processIds;
                }
            }
            catch (IOException)
            {
                // The child may still be publishing the file; retry briefly.
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("Fake child did not publish both PIDs in time.");
    }

    private static bool TryReadProcessIds(string pidFile, out FakeProcessIds processIds)
    {
        processIds = default;
        if (!File.Exists(pidFile))
        {
            return false;
        }

        var parts = File.ReadAllText(pidFile)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var bashProcessId) ||
            !int.TryParse(parts[1], out var childProcessId) ||
            bashProcessId <= 0 ||
            childProcessId <= 0 ||
            bashProcessId == childProcessId
        )
        {
            return false;
        }

        processIds = new FakeProcessIds(bashProcessId, childProcessId);
        return true;
    }

    private static Task AssertProcessesDisappearAsync(FakeProcessIds processIds)
    {
        return Task.WhenAll(
            AssertProcessDisappearsAsync(processIds.BashProcessId),
            AssertProcessDisappearsAsync(processIds.ChildProcessId)
        );
    }

    // Kill(true) sends SIGKILL but returns before the kernel reaps the process, so
    // /proc/{pid} can linger momentarily; poll briefly instead of asserting instantly.
    private static async Task AssertProcessDisappearsAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (ProcessExists(processId))
        {
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Process {processId} still present in /proc {stopwatch.Elapsed} after kill."
            );
            await Task.Delay(20);
        }
    }

    private static bool ProcessExists(int processId)
    {
        return Directory.Exists($"/proc/{processId}");
    }

    private static void TryKillProcesses(FakeProcessIds? processIds)
    {
        if (processIds is not { } ids)
        {
            return;
        }

        TryKillProcess(ids.BashProcessId);
        TryKillProcess(ids.ChildProcessId);
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(true);
        }
        catch
        {
            // The expected path already reaped the fake child.
        }
    }

    private readonly record struct FakeProcessIds(int BashProcessId, int ChildProcessId);

    private sealed record UnsupportedProcessInput : ProcessInput;

    private sealed class ObservedArguments(
        IReadOnlyList<string> values,
        Action onRead
    ) : IReadOnlyList<string>
    {
        public int Count
        {
            get
            {
                onRead();
                return values.Count;
            }
        }

        public string this[int index]
        {
            get
            {
                onRead();
                return values[index];
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            onRead();
            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
