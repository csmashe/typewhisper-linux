using System.Diagnostics;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_returns_success_when_descendant_holds_stdout_open()
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
                timeout: TimeSpan.FromSeconds(2)
            );
            childProcessId = await WaitForProcessIdAsync(pidFile);

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < s_testGuard,
                $"ProcessRunner did not bound the output drain; elapsed {stopwatch.Elapsed}."
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

    private static string NewPidFile()
    {
        return Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-{Guid.NewGuid():N}.pid"
        );
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
