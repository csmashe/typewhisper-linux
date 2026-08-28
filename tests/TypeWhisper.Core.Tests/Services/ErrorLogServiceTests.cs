using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="ErrorLogService" />'s contract that reporting an error never throws at the caller.</summary>
public sealed class ErrorLogServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.ErrorLogServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    [Fact]
    public void AddEntry_PersistsBeforePublishingAndRoundTrips()
    {
        var directory = TestPaths.CreateTempDirectory("ErrorLogServiceRoundTrip");
        try
        {
            var service = new ErrorLogService(directory);
            var changed = 0;
            service.EntriesChanged += () => changed++;

            service.AddEntry("failure");

            Assert.Equal("failure", Assert.Single(service.Entries).Message);
            Assert.Equal(1, changed);
            Assert.Equal(
                "failure",
                Assert.Single(new ErrorLogService(directory).Entries).Message
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Construction_WhenLogIsUnreadable_DegradesToEmptyWithoutClobberingIt()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory("ErrorLogServiceUnreadable");
        var path = Path.Join(directory, "error-log.json");
        File.WriteAllText(path, "[]");
        var originalMode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);

            var service = new ErrorLogService(directory);

            Assert.Empty(service.Entries);
            Assert.Null(Record.Exception(() => service.AddEntry("dropped")));
            Assert.Empty(service.Entries);

            File.SetUnixFileMode(path, originalMode);
            // Outside the finally, so a failure above stays the reported failure.
            Assert.Equal("[]", File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AddEntry_WhenPersistenceFails_IsBestEffortWithoutPublishingOrEvent()
    {
        // Recreate a path whose destination name itself is at the filesystem limit.
        // ErrorLogService appends a fixed leaf, so make the directory read-only instead —
        // which root ignores, so skip there too rather than assert a write that succeeds.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var failurePath = new AtomicWriteFailureTestPath("[]");
        var directory = Path.GetDirectoryName(failurePath.FilePath)!;
        var expectedPath = Path.Join(directory, "error-log.json");
        File.Move(failurePath.FilePath, expectedPath);

        var originalMode = File.GetUnixFileMode(directory);
        try
        {
            var service = new ErrorLogService(directory);
            var changed = false;
            service.EntriesChanged += () => changed = true;
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
            );

            var exception = Record.Exception(() => service.AddEntry("not committed"));

            Assert.Null(exception);
            Assert.Empty(service.Entries);
            Assert.False(changed);
        }
        finally
        {
            File.SetUnixFileMode(directory, originalMode);
        }
    }

    [Fact]
    public void AddEntry_WhenASubscriberThrows_StillRecordsAndDoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        var secondSubscriberRan = false;
        sut.EntriesChanged += () => throw new InvalidOperationException("subscriber failed");
        sut.EntriesChanged += () => secondSubscriberRan = true;

        sut.AddEntry("something went wrong");

        // The throwing subscriber must not starve the one registered after it.
        Assert.True(secondSubscriberRan);
        Assert.Contains(sut.Entries, e => e.Message == "something went wrong");
    }

    [Fact]
    public void AddEntry_WhenASubscriberThrowsAnUnprintableException_DoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        sut.EntriesChanged += () => throw new UnprintableException();

        // ToString is virtual, so formatting the failure must not become the failure.
        var thrown = Record.Exception(() => sut.AddEntry("something went wrong"));

        Assert.Null(thrown);
    }

    [Fact]
    public void ClearAll_WhenASubscriberThrows_DoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        sut.AddEntry("something went wrong");
        sut.EntriesChanged += () => throw new InvalidOperationException("subscriber failed");

        var thrown = Record.Exception(sut.ClearAll);

        Assert.Null(thrown);
        Assert.Empty(sut.Entries);
    }

    private sealed class UnprintableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("cannot render");

        public override string Message => throw new InvalidOperationException("cannot render");
    }
}
