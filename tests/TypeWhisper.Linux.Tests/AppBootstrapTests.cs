using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppBootstrapTests
{
    public static TheoryData<string, string?, string?> ProductionStageFailures =>
        new()
        {
            { App.BootstrapStageNames.HistoryLoad, null, null },
            { App.BootstrapStageNames.SessionCleanup, null, null },
            { App.BootstrapStageNames.AudioConfiguration, null, null },
            {
                App.BootstrapStageNames.BundledPluginDeployment,
                App.BootstrapStageNames.PluginInitialization,
                App.BootstrapStageNames.WatchFolderAutoStart
            },
            {
                App.BootstrapStageNames.PluginInitialization,
                App.BootstrapStageNames.WatchFolderAutoStart,
                null
            },
            { App.BootstrapStageNames.RetentionInitialization, null, null },
            {
                App.BootstrapStageNames.ModelMigration,
                App.BootstrapStageNames.ModelAutoLoad,
                null
            },
            { App.BootstrapStageNames.ModelAutoLoad, null, null },
            { App.BootstrapStageNames.WatchFolderAutoStart, null, null },
        };

    [Theory]
    [MemberData(nameof(ProductionStageFailures))]
    public async Task RunAsync_WhenEachProductionStageFails_RunsIndependentStagesAndSkipsDependents(
        string failingStage,
        string? firstExpectedSkippedStage,
        string? secondExpectedSkippedStage
    )
    {
        var attempted = new List<string>();
        var failure = new InvalidOperationException($"Failure in {failingStage}");
        var stages = CreateProductionShapedStages(attempted, failingStage, failure);
        var expectedSkippedStages = new[]
        {
            firstExpectedSkippedStage,
            secondExpectedSkippedStage,
        }.OfType<string>().ToArray();

        var report = await new App.BootstrapRunner(stages).RunAsync();

        var expectedAttempted = stages
            .Select(stage => stage.Name)
            .Where(name => !expectedSkippedStages.Contains(name, StringComparer.Ordinal));
        Assert.Equal(expectedAttempted, attempted);
        Assert.Equal(
            expectedSkippedStages,
            report
                .Outcomes.Where(outcome =>
                    outcome.Status == App.BootstrapStageStatus.Skipped
                )
                .Select(outcome => outcome.Name)
        );

        foreach (var outcome in report.Outcomes)
        {
            var expectedStatus =
                outcome.Name == failingStage
                    ? App.BootstrapStageStatus.Failed
                    : expectedSkippedStages.Contains(
                        outcome.Name,
                        StringComparer.Ordinal
                    )
                        ? App.BootstrapStageStatus.Skipped
                        : App.BootstrapStageStatus.Succeeded;
            Assert.Equal(expectedStatus, outcome.Status);
        }
    }

    [Fact]
    public void CreateBootstrapStages_WatchFolderAutoStart_IsAfterModelAutoLoadAndDependsOnlyOnPluginInitialization()
    {
        var stages = App.CreateBootstrapStages(new UnusedServiceProvider());
        var watchFolderAutoStart = Assert.Single(
            stages,
            stage => stage.Name == App.BootstrapStageNames.WatchFolderAutoStart
        );

        Assert.Equal(
            [App.BootstrapStageNames.PluginInitialization],
            watchFolderAutoStart.Dependencies
        );
        Assert.True(
            stages.Index()
                .Single(item => item.Item.Name == App.BootstrapStageNames.ModelAutoLoad)
                .Index
            < stages.Index()
                .Single(item =>
                    item.Item.Name == App.BootstrapStageNames.WatchFolderAutoStart
                )
                .Index
        );
    }

    [Fact]
    public async Task RunAsync_NonRequiredFailure_CapturesExceptionAndDoesNotThrow()
    {
        var failure = new InvalidOperationException("injected failure");
        var laterStageRan = false;
        var errorLog = new RecordingErrorLogService();
        App.BootstrapStage[] stages =
        [
            new("Failing stage", [], () => Task.FromException(failure), Required: false),
            new(
                "Later stage",
                [],
                () =>
                {
                    laterStageRan = true;
                    return Task.CompletedTask;
                },
                Required: false
            ),
        ];

        var report = await new App.BootstrapRunner(stages, errorLog).RunAsync();

        var failed = Assert.Single(
            report.Outcomes,
            outcome => outcome.Status == App.BootstrapStageStatus.Failed
        );
        Assert.Equal("Failing stage", failed.Name);
        Assert.Same(failure, failed.Exception);
        Assert.True(laterStageRan);
        Assert.True(report.IsDegraded);
        Assert.Empty(report.RequiredFailures);
        Assert.Equal(
            [
                (
                    "Bootstrap stage 'Failing stage' failed: injected failure",
                    ErrorCategory.General
                ),
            ],
            errorLog.AddedEntries
        );
    }

    [Fact]
    public async Task RunAsync_AllProductionStagesSucceed_RunsOnceInOrderAndReportsSuccess()
    {
        var runOrder = new List<string>();
        var stages = CreateProductionShapedStages(runOrder);

        var report = await new App.BootstrapRunner(stages).RunAsync();

        Assert.Equal(stages.Select(stage => stage.Name), runOrder);
        Assert.All(
            stages,
            stage => Assert.Equal(1, runOrder.Count(name => name == stage.Name))
        );
        Assert.Equal(
            stages.Select(stage => stage.Name),
            report.Outcomes.Select(outcome => outcome.Name)
        );
        Assert.All(
            report.Outcomes,
            outcome => Assert.Equal(App.BootstrapStageStatus.Succeeded, outcome.Status)
        );
        Assert.False(report.IsDegraded);
        Assert.Empty(report.RequiredFailures);
    }

    [Fact]
    public async Task RunAsync_DependencyChainFailure_SkipsTransitiveDependentsWithReasons()
    {
        var runOrder = new List<string>();
        App.BootstrapStage[] stages =
        [
            new(
                "A",
                [],
                () =>
                {
                    runOrder.Add("A");
                    throw new InvalidOperationException("A failed");
                },
                Required: false
            ),
            new(
                "B",
                ["A"],
                () =>
                {
                    runOrder.Add("B");
                    return Task.CompletedTask;
                },
                Required: false
            ),
            new(
                "C",
                ["B"],
                () =>
                {
                    runOrder.Add("C");
                    return Task.CompletedTask;
                },
                Required: false
            ),
        ];

        var report = await new App.BootstrapRunner(stages).RunAsync();

        Assert.Equal(["A"], runOrder);
        Assert.Equal(App.BootstrapStageStatus.Failed, Outcome("A").Status);
        Assert.Equal(App.BootstrapStageStatus.Skipped, Outcome("B").Status);
        Assert.Equal("A", Outcome("B").SkippedDueTo);
        Assert.Equal(App.BootstrapStageStatus.Skipped, Outcome("C").Status);
        Assert.Equal("B", Outcome("C").SkippedDueTo);

        App.BootstrapStageOutcome Outcome(string name)
        {
            return Assert.Single(report.Outcomes, outcome => outcome.Name == name);
        }
    }

    [Fact]
    public async Task RunAsync_RequiredFailure_RunsIndependentStagesThenThrowsWithReport()
    {
        var independentStageRan = false;
        App.BootstrapStage[] stages =
        [
            new(
                "Required stage",
                [],
                () => Task.FromException(new InvalidOperationException("required failure")),
                Required: true
            ),
            new(
                "Independent stage",
                [],
                () =>
                {
                    independentStageRan = true;
                    return Task.CompletedTask;
                },
                Required: false
            ),
        ];
        var runner = new App.BootstrapRunner(stages);

        var exception = await Assert.ThrowsAsync<App.RequiredBootstrapStageException>(
            runner.RunAsync
        );

        Assert.True(independentStageRan);
        Assert.Equal(
            ["Required stage"],
            exception.Report.RequiredFailures.Select(outcome => outcome.Name)
        );
        Assert.Equal(2, exception.Report.Outcomes.Count);
    }

    [Fact]
    public void RouteRecentTranscriptionFeedback_WhenIdle_PublishesThroughFeedbackStatePath()
    {
        var publications = new List<(string Message, bool IsError)>();
        var errorLog = new RecordingErrorLogService();

        var published = App.RouteRecentTranscriptionFeedback(
            (message, isError) =>
            {
                if (!DictationOrchestrator.CanPublishTransientFeedback(false))
                {
                    return false;
                }

                publications.Add((message, isError));
                return true;
            },
            errorLog,
            "localized success",
            false
        );

        Assert.True(published);
        Assert.Equal([("localized success", false)], publications);
        Assert.Empty(errorLog.AddedEntries);
    }

    [Fact]
    public void RouteRecentTranscriptionFeedback_WhenDictationActive_SkipsPublicationButLogsEnglishError()
    {
        var publications = new List<(string Message, bool IsError)>();
        var errorLog = new RecordingErrorLogService();

        var published = App.RouteRecentTranscriptionFeedback(
            (message, isError) =>
            {
                if (!DictationOrchestrator.CanPublishTransientFeedback(true))
                {
                    return false;
                }

                publications.Add((message, isError));
                return true;
            },
            errorLog,
            "lokalisierter Fehler",
            true
        );

        Assert.False(published);
        Assert.Empty(publications);
        Assert.Equal(
            [
                (
                    "Recent transcription insertion failed. Install wl-clipboard on Wayland or xclip on X11 for clipboard access. For automatic paste, set up ydotool on GNOME/KDE Wayland, install wtype or ydotool on other Wayland compositors, or install xdotool on X11.",
                    ErrorCategory.Insertion
                ),
            ],
            errorLog.AddedEntries
        );
    }

    private static App.BootstrapStage[] CreateProductionShapedStages(
        List<string> attempted,
        string? failingStage = null,
        Exception? failure = null
    )
    {
        return App.CreateBootstrapStages(new UnusedServiceProvider())
            .Select(stage =>
                stage
                    with
                    {
                        Action = () =>
                        {
                            attempted.Add(stage.Name);
                            return stage.Name == failingStage
                                ? Task.FromException(
                                    failure
                                        ?? new InvalidOperationException(
                                            $"Failure in {failingStage}"
                                        )
                                )
                                : Task.CompletedTask;
                        },
                    }
            )
            .ToArray();
    }

    private sealed class UnusedServiceProvider : IServiceProvider
    {
        // ReSharper disable once ReturnTypeCanBeNotNullable -- implements IServiceProvider.GetService, whose contract is nullable.
        public object? GetService(Type serviceType)
        {
            throw new InvalidOperationException(
                $"Production action unexpectedly resolved {serviceType.Name}."
            );
        }
    }

    private sealed class RecordingErrorLogService : IErrorLogService
    {
        public List<(string Message, string Category)> AddedEntries { get; } = [];

        public IReadOnlyList<ErrorLogEntry> Entries => [];

        public event Action? EntriesChanged;

        public void AddEntry(string message, string category = ErrorCategory.General)
        {
            AddedEntries.Add((message, category));
            EntriesChanged?.Invoke();
        }

        public void ClearAll()
        {
            AddedEntries.Clear();
            EntriesChanged?.Invoke();
        }

        public string ExportDiagnostics()
        {
            return string.Empty;
        }
    }
}
