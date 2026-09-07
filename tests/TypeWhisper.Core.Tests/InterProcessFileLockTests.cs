using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Core.Tests;

/// <summary>
///     Validates the mutual-exclusion + cancellation contract the resumable GPU downloaders
///     rely on. The type is file-linked into this project (see the .csproj).
/// </summary>
public sealed class InterProcessFileLockTests
{
    private static readonly TimeSpan s_coordinationTimeout = TimeSpan.FromSeconds(5);

    private static string NewTempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), "tw-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SecondAcquire_WaitsUntilFirstReleases()
    {
        var dir = NewTempDir();
        using var acquisitionCts = new CancellationTokenSource();
        FileStream? first = null;
        FileStream? acquired = null;
        Task<FileStream>? firstAcquisition = null;
        Task<FileStream>? second = null;
        try
        {
            var lockPath = Path.Join(dir, "artifact.lock");
            firstAcquisition = InterProcessFileLock.AcquireAsync(
                lockPath,
                acquisitionCts.Token
            );
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; wiring acquisitionCts.Token here would make the deadline racy with the finally's teardown cancel instead of fixed.
            first = await firstAcquisition.WaitAsync(s_coordinationTimeout);

            second = InterProcessFileLock.AcquireAsync(lockPath, acquisitionCts.Token);
            // The second acquire must NOT complete while the first holds the lock.
            // ReSharper disable once MethodSupportsCancellation -- Task.Delay is a fixed probe window proving the second acquire stays pending; a token would defeat the check.
            var winner = await Task.WhenAny(second, Task.Delay(500));
            Assert.NotSame(second, winner);

            // Releasing the first lets the second acquire (within a poll interval).
            await first.DisposeAsync();
            first = null;
            firstAcquisition = null;
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; wiring acquisitionCts.Token here would make the deadline racy with the finally's teardown cancel instead of fixed.
            acquired = await second.WaitAsync(s_coordinationTimeout);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel is the teardown signal; CancelAsync buys nothing in cleanup.
            acquisitionCts.Cancel();
            first ??= await CompleteAcquireBestEffort(firstAcquisition);
            try
            {
                if (first is not null)
                    await first.DisposeAsync();
            }
            finally
            {
                acquired ??= await CompleteAcquireBestEffort(second);
                try
                {
                    if (acquired is not null)
                        await acquired.DisposeAsync();
                }
                finally
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    private static async Task<FileStream?> CompleteAcquireBestEffort(
        Task<FileStream>? acquisition
    )
    {
        if (acquisition is null)
            return null;

        try
        {
            return await acquisition.WaitAsync(s_coordinationTimeout);
        }
        catch
        {
            // Best-effort bounded observation after cancellation.
            return null;
        }
    }

    [Fact]
    public async Task Acquire_IsCancellable_WhileWaiting()
    {
        var dir = NewTempDir();
        try
        {
            var lockPath = Path.Join(dir, "artifact.lock");
            await using var first = await InterProcessFileLock.AcquireAsync(lockPath, CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var waiting = InterProcessFileLock.AcquireAsync(lockPath, cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
