using System.Diagnostics;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.ProcessTestChild;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProviderProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_cancellation_returns_provider_miss_after_reaping()
    {
        var pidPath = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-provider-{Guid.NewGuid():N}.pid"
        );
        using var cts = new CancellationTokenSource();
        int? processId = null;
        try
        {
            var run = new ProviderProcessRunner(
                new TypeWhisper.Linux.Services.ProcessRunner()
            ).RunAsync(
                "dotnet",
                [
                    typeof(ProcessTestChildMarker).Assembly.Location,
                    "wait",
                    pidPath,
                ],
                cts.Token
            );
            processId = await WaitForProcessIdAsync(pidPath);
            // ReSharper disable once MethodHasAsyncOverload -- the test deliberately triggers synchronous CTS cancellation to exercise the cancel-and-kill path.
            cts.Cancel();

            // ReSharper disable once MethodSupportsCancellation -- WaitAsync uses only the test-guard timeout; there is no ambient cancellation token to pass here.
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(-1, result.ExitCode);
            Assert.Null(result.StdOut);
            await AssertProcessDisappearsAsync(processId.Value);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous cancellation is the intended cleanup here.
            cts.Cancel();
            if (processId is { } leaked)
            {
                TryKill(leaked);
            }

            File.Delete(pidPath);
        }
    }

    private static async Task<int> WaitForProcessIdAsync(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                File.Exists(path)
                && int.TryParse(await File.ReadAllTextAsync(path), out var processId)
            )
            {
                return processId;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("Provider child did not publish its PID.");
    }

    private static async Task AssertProcessDisappearsAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (Directory.Exists($"/proc/{processId}"))
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            await Task.Delay(20);
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(true);
        }
        catch
        {
            // Expected when the supervisor already reaped it.
        }
    }
}
