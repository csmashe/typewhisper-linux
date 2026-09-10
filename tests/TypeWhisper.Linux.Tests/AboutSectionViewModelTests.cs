using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AboutSectionViewModelTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.AboutSectionViewModelTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void FilteredErrorEntries_ExposesLocalPresentationTimestamp()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "About tests UTC+13",
            TimeSpan.FromHours(13),
            "About tests UTC+13",
            "About tests UTC+13"
        );
        var entry = new ErrorLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = new DateTime(2030, 1, 2, 23, 30, 0, DateTimeKind.Utc),
            Message = "test error",
            Category = ErrorCategory.General,
        };
        var errorLog = new Mock<IErrorLogService>();
        errorLog.SetupGet(service => service.Entries).Returns([entry]);
        var preferences = new LinuxPreferencesService(
            Path.Join(_tempDir, "linux-preferences.json")
        );
        var sut = new AboutSectionViewModel(
            errorLog.Object,
            new SettingsBackupService(_tempDir),
            new UpdateCheckService(preferences),
            timeZone
        );

        var presentationEntry = Assert.Single(sut.FilteredErrorEntries);
        Assert.Same(entry, presentationEntry.Record);
        Assert.Equal(
            new DateTime(2030, 1, 3, 12, 30, 0),
            presentationEntry.LocalTimestamp
        );
    }
}
