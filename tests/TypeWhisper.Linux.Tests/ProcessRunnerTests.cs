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

            var processId = await ReadProcessIdAsync(pidFile);
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

            var processId = await ReadProcessIdAsync(pidFile);
            await AssertProcessDisappearsAsync(processId);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_kills_process_and_propagates_when_caller_cancels()
    {
        var pidFile = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-process-runner-{Guid.NewGuid():N}.pid"
        );
        try
        {
            using var cts = new CancellationTokenSource();
            var runTask = new ProcessRunner().RunAsync(
                "/bin/bash",
                ["-c", "printf '%s' \"$$\" > \"$1\"; sleep 30", "process-runner-test", pidFile],
                ct: cts.Token
            );

            var processId = await ReadProcessIdAsync(pidFile);
            await cts.CancelAsync();

            // Cancellation must surface, not be flattened into a NotStarted result.
            // ReSharper disable once MethodSupportsCancellation -- WaitAsync here is only a hang-guard; passing cts.Token would satisfy the OperationCanceledException assertion via the guard's own cancellation instead of the run under test.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(s_testGuard)
            );
            await AssertProcessDisappearsAsync(processId);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    // The child creates the pid file before writing to it, so a read can land on an empty
    // file; poll until it holds a parsable id rather than throwing on the first read.
    private static async Task<int> ReadProcessIdAsync(string pidFile)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(pidFile)
                && int.TryParse(await File.ReadAllTextAsync(pidFile), out var processId))
            {
                return processId;
            }

            Assert.True(
                stopwatch.Elapsed < s_testGuard,
                $"Process id file '{pidFile}' never contained a parsable id."
            );
            await Task.Delay(20);
        }
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
}
