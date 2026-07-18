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
        var stopwatch = Stopwatch.StartNew();
        var runTask = new ProcessRunner().RunAsync(
            "/bin/bash",
            ["-c", "sleep 30 & exit 0"],
            timeout: TimeSpan.FromSeconds(2)
        );

        var completedTask = await Task.WhenAny(runTask, Task.Delay(s_testGuard));
        stopwatch.Stop();

        Assert.True(
            ReferenceEquals(runTask, completedTask) && stopwatch.Elapsed < s_testGuard,
            $"ProcessRunner did not bound the output drain; elapsed {stopwatch.Elapsed}."
        );
        var result = await runTask;
        Assert.True(result.Succeeded);
        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
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
        var pidFile = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-{Guid.NewGuid():N}.pid"
        );
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", "printf '%s' \"$$\" > \"$1\"; sleep 30", "process-runner-test", pidFile],
                timeout: TimeSpan.FromSeconds(1)
            );

            var result = await runTask.WaitAsync(s_testGuard);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < s_testGuard);
            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.ExitCode);

            var processId = int.Parse(await File.ReadAllTextAsync(pidFile));
            await AssertProcessDisappearsAsync(processId);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_times_out_when_child_does_not_read_standard_input()
    {
        var pidFile = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-{Guid.NewGuid():N}.pid"
        );
        try
        {
            var standardInput = new string('x', 256 * 1024);
            var stopwatch = Stopwatch.StartNew();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", "printf '%s' \"$$\" > \"$1\"; sleep 30", "process-runner-test", pidFile],
                standardInput: standardInput,
                timeout: TimeSpan.FromSeconds(2)
            );

            var completedTask = await Task.WhenAny(runTask, Task.Delay(s_testGuard));
            stopwatch.Stop();

            Assert.True(
                ReferenceEquals(runTask, completedTask) && stopwatch.Elapsed < s_testGuard,
                $"ProcessRunner did not bound the stdin write; elapsed {stopwatch.Elapsed}."
            );
            var result = await runTask;
            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.ExitCode);

            var processId = int.Parse(await File.ReadAllTextAsync(pidFile));
            await AssertProcessDisappearsAsync(processId);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_kills_and_rethrows_on_caller_cancellation()
    {
        var pidFile = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-cancellation-{Guid.NewGuid():N}.pid"
        );
        using var cts = new CancellationTokenSource();
        int? processId = null;
        try
        {
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", "printf '%s' \"$$\" > \"$1\"; sleep 30", "process-runner-test", pidFile],
                timeout: TimeSpan.FromSeconds(30),
                ct: cts.Token
            );
            processId = await WaitForProcessIdAsync(pidFile);

            // ReSharper disable once MethodHasAsyncOverload -- the test deliberately triggers synchronous CTS cancellation to exercise ProcessRunner's cancel-and-kill path.
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
                async () => await runTask.WaitAsync(s_testGuard)
            );
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here; no async continuation to await in the finally guard.
            cts.Cancel();
            if (processId is { } leakedProcessId)
            {
                TryKillProcess(leakedProcessId);
            }

            File.Delete(pidFile);
        }
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
                    if (int.TryParse(contents, out var processId))
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

    // Kill(true) sends SIGKILL but returns before the kernel reaps the process, so
    // /proc/{pid} can linger momentarily; poll briefly instead of asserting instantly.
    private static async Task AssertProcessDisappearsAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (Directory.Exists($"/proc/{processId}"))
        {
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Process {processId} still present in /proc {stopwatch.Elapsed} after kill."
            );
            await Task.Delay(20);
        }
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
}
