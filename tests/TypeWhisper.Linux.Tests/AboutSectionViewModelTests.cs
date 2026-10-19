using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Telemetry;
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
    public async Task ResetAllData_RecordsTheResetAndRestartsTheApp()
    {
        var dataDir = Path.Join(_tempDir, "data");
        var restarts = 0;
        var reset = new LocalDataResetService(dataDir);
        var sut = CreateViewModel(new FakeProcessRunner(), dataReset: reset, requestRestart: () => ++restarts > 0);
        sut.DeleteModelsOnReset = true;

        await sut.ResetAllDataAsync();

        Assert.True(reset.IsResetPending);
        Assert.Equal(1, restarts);
        Assert.Contains("\"deleteModels\": true", await File.ReadAllTextAsync(Path.Join(dataDir, LocalDataResetService.PendingMarkerName)));
        // The card stays busy until the app exits.
        Assert.True(sut.IsBackupBusy);
    }

    [Fact]
    public async Task ResetAllData_WhenItCannotBeRecorded_DoesNotRestart()
    {
        var blocker = Path.Join(_tempDir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "");
        var restarts = 0;
        var sut = CreateViewModel(
            new FakeProcessRunner(),
            dataReset: new LocalDataResetService(Path.Join(blocker, "data")),
            requestRestart: () => ++restarts > 0
        );

        await Assert.ThrowsAnyAsync<IOException>(sut.ResetAllDataAsync);

        Assert.Equal(0, restarts);
        Assert.False(sut.IsBackupBusy);
        Assert.Equal(Loc.Instance["About.ResetFailed"], sut.BackupStatusText);
    }

    [Fact]
    public async Task ResetAllData_WhileQuitting_RunsAtTheNextLaunch()
    {
        var sut = CreateViewModel(
            new FakeProcessRunner(),
            dataReset: new LocalDataResetService(Path.Join(_tempDir, "data")),
            requestRestart: () => false
        );

        await sut.ResetAllDataAsync();

        Assert.Equal(Loc.Instance["About.ResetAtNextLaunch"], sut.BackupStatusText);
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

    [Fact]
    public async Task ExportSupportReport_WritesTheReportAndReportsMissingSections()
    {
        var destination = Path.Join(_tempDir, "support.json");
        var sut = CreateViewModel(new FakeProcessRunner(), supportReport: SupportReport(() => throw new IOException("private")));

        await sut.ExportSupportReportAsync(destination);

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "historyCount",
            Assert.Single(document.RootElement.GetProperty("collectionErrors").EnumerateArray())
                .GetProperty("section").GetString()
        );
        Assert.Equal(
            Loc.Instance["About.SupportReportPartial"],
            sut.SupportReportStatusText
        );
        Assert.False(sut.IsSupportReportBusy);
    }

    [Fact]
    public async Task ExportSupportReport_ReportsAnUnwritableDestination()
    {
        var sut = CreateViewModel(new FakeProcessRunner(), supportReport: SupportReport(() => 0));

        await sut.ExportSupportReportAsync(Path.Join(_tempDir, "missing", "support.json"));

        Assert.Equal(
            Loc.Instance["About.SupportReportFailed"],
            sut.SupportReportStatusText
        );
    }

    private static SupportReportService SupportReport(Func<int> historyCount) =>
        new(new SupportReportService.Sources(
            () => AppSettings.Default,
            () => new HashSet<string>(),
            _ => new TypeWhisper.Core.Services.Diagnostics.SupportReport.ModelInfo(null, null, false, "None", false, false, "auto", null),
            () => new TypeWhisper.Core.Services.Diagnostics.SupportReport.ApiInfo(false, false, 9876, false),
            () => SupportReportService.SummarizeAudio([], true, false, AppSettings.Default),
            trusted => SupportReportService.SummarizePlugins([], new SupportReportService.PluginRoles([], [], [], [], []), 0, trusted),
            () => [],
            () => [],
            historyCount,
            () => [],
            () => [],
            () => 0,
            () => Task.FromResult<TypeWhisper.Core.Services.Diagnostics.SupportReport.LogInfo?>(null)
        ));

    private AboutSectionViewModel CreateViewModel(
        FakeProcessRunner runner,
        LocalDataExportService? dataExport = null,
        SupportReportService? supportReport = null,
        LocalDataResetService? dataReset = null,
        Func<bool>? requestRestart = null
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
            dataExport,
            supportReport,
            dataReset ?? new LocalDataResetService(Path.Join(_tempDir, "reset-data")),
            requestRestart ?? (() => true)
        );
    }
}
