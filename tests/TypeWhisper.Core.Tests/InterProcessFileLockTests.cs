using System.IO;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Core.Tests;

// Validates the mutual-exclusion + cancellation contract the resumable GPU downloaders
// rely on. The type is file-linked into this project (see the .csproj).
public sealed class InterProcessFileLockTests
{
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
        try
        {
            var lockPath = Path.Join(dir, "artifact.lock");
            var first = await InterProcessFileLock.AcquireAsync(lockPath, default);

            var second = InterProcessFileLock.AcquireAsync(lockPath, CancellationToken.None);
            // The second acquire must NOT complete while the first holds the lock.
            var winner = await Task.WhenAny(second, Task.Delay(500));
            Assert.NotSame(second, winner);

            // Releasing the first lets the second acquire (within a poll interval).
            await first.DisposeAsync();
            var acquired = await second;
            await acquired.DisposeAsync();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Acquire_IsCancellable_WhileWaiting()
    {
        var dir = NewTempDir();
        try
        {
            var lockPath = Path.Join(dir, "artifact.lock");
            await using var first = await InterProcessFileLock.AcquireAsync(lockPath, default);

            using var cts = new CancellationTokenSource();
            var waiting = InterProcessFileLock.AcquireAsync(lockPath, cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
