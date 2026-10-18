using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.UserData;
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

    [Fact]
    public void ProjectLinkCommands_LaunchExpectedUrls()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateViewModel(runner);

        sut.OpenProjectCommand.Execute(null);
        sut.OpenDocsCommand.Execute(null);
        sut.OpenIssuesCommand.Execute(null);
        sut.OpenLicenseCommand.Execute(null);
        sut.OpenUpstreamCommand.Execute(null);

        Assert.Equal(
            new[]
            {
                new Uri(AboutSectionViewModel.ProjectUrl),
                new Uri(AboutSectionViewModel.DocsUrl),
                new Uri(AboutSectionViewModel.IssuesUrl),
                new Uri(AboutSectionViewModel.LicenseUrl),
                new Uri(AboutSectionViewModel.UpstreamUrl),
            },
            runner.LaunchedUris
        );
    }

    [Fact]
    public async Task ExportAllData_ReportsTheResultAndReleasesTheCard()
    {
        var dataDir = Path.Join(_tempDir, "data");
        Directory.CreateDirectory(Path.Join(dataDir, "Data"));
        await File.WriteAllTextAsync(Path.Join(dataDir, "Data", "history.json"), "[]");
        var sut = CreateViewModel(new FakeProcessRunner(), new LocalDataExportService(dataDir));

        var result = await sut.ExportAllDataAsync(Path.Join(_tempDir, "export.zip"));

        Assert.Equal(1, result.FileCount);
        Assert.Equal(Loc.Instance.GetString("About.DataExported", 1), sut.BackupStatusText);
        Assert.False(sut.IsBackupBusy);
        Assert.False(sut.IsExportingData);
    }

    [Fact]
    public async Task CancelDataExport_StopsTheExportWithoutWritingAFile()
    {
        var dataDir = Path.Join(_tempDir, "data");
        Directory.CreateDirectory(Path.Join(dataDir, "Data"));
        await File.WriteAllTextAsync(Path.Join(dataDir, "Data", "history.json"), "[]");
        AboutSectionViewModel? sut = null;
        sut = CreateViewModel(
            new FakeProcessRunner(),
            // ReSharper disable once AccessToModifiedClosure -- reads the view model assigned just below, once the export runs.
            new LocalDataExportService(dataDir, _ => sut!.CancelDataExportCommand.Execute(null))
        );
        var destination = Path.Join(_tempDir, "export.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExportAllDataAsync(destination));

        Assert.False(File.Exists(destination));
        Assert.Equal(Loc.Instance["About.DataExportCanceled"], sut.BackupStatusText);
        Assert.False(sut.IsBackupBusy);
    }

    [Fact]
    public void InstallAndDataLocations_AreRootedPaths()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateViewModel(runner);

        Assert.False(string.IsNullOrWhiteSpace(sut.InstallLocation));
        Assert.True(Path.IsPathRooted(sut.InstallLocation));
        Assert.False(string.IsNullOrWhiteSpace(sut.DataLocation));
        Assert.True(Path.IsPathRooted(sut.DataLocation));
    }

    private AboutSectionViewModel CreateViewModel(
        FakeProcessRunner runner,
        LocalDataExportService? dataExport = null
    )
    {
        var errorLog = new Mock<IErrorLogService>();
        errorLog.SetupGet(service => service.Entries).Returns([]);
        var preferences = new LinuxPreferencesService(
            Path.Join(_tempDir, "linux-preferences.json")
        );
        return new AboutSectionViewModel(
            errorLog.Object,
            new SettingsBackupService(_tempDir),
            new UpdateCheckService(preferences),
            new UrlLauncher(runner),
            TimeZoneInfo.Utc,
            dataExport
        );
    }
}
