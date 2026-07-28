using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

public sealed class ErrorLogServiceTests
{
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
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
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
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
            Assert.Equal("[]", File.ReadAllText(path));
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AddEntry_WhenPersistenceFails_IsBestEffortWithoutPublishingOrEvent()
    {
        using var failurePath = new AtomicWriteFailureTestPath("[]");
        var directory = Path.GetDirectoryName(failurePath.FilePath)!;
        var expectedPath = Path.Join(directory, "error-log.json");
        File.Move(failurePath.FilePath, expectedPath);
        // Recreate a path whose destination name itself is at the filesystem limit.
        // ErrorLogService appends a fixed leaf, so make the directory read-only instead.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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
}
