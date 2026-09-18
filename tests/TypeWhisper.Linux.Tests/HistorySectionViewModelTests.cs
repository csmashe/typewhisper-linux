using System.Runtime.Serialization;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;
using static TypeWhisper.Linux.Tests.SpeechFeedbackServiceTests;

namespace TypeWhisper.Linux.Tests;

public sealed class HistorySectionViewModelTests : IDisposable
{
    private static readonly TimeSpan s_readbackGuard = TimeSpan.FromSeconds(5);

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.HistorySectionViewModelTests"
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
    public void Selection_SurvivesRefreshAndDropsDeletedIds()
    {
        var history = CreateHistoryService();
        var record = CreateRecord("selected");
        history.AddRecord(record);
        var sut = CreateViewModel(history, CreateDictionaryService());
        sut.ToggleSelectingCommand.Execute(null);
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        row.IsSelected = true;

        history.AddRecord(CreateRecord("later"));
        RefreshHistory(sut);

        Assert.True(row.IsSelected);
        Assert.Equal([record.Id], sut.SnapshotSelectedIds());
        Assert.Equal(1, sut.SelectedCount);
        history.DeleteRecord(record.Id);
        RefreshHistory(sut);

        Assert.Empty(sut.SnapshotSelectedIds());
        Assert.Equal(0, sut.SelectedCount);
        Assert.False(sut.HasSelection);
    }

    [Fact]
    public void SelectAllShown_SelectsOnlyFilteredRecords()
    {
        var history = CreateHistoryService();
        for (var i = 0; i < 45; i++)
        {
            history.AddRecord(CreateRecord($"matching {i}"));
        }

        history.AddRecord(CreateRecord("unrelated"));
        var sut = CreateViewModel(history, CreateDictionaryService());
        sut.SearchQuery = "matching";
        sut.ToggleSelectingCommand.Execute(null);
        Assert.True(sut.HasMore);

        sut.SelectAllShownCommand.Execute(null);

        Assert.Equal(45, sut.SelectedCount);
        Assert.Equal(
            history.Records.Where(record => record.FinalText.StartsWith("matching", StringComparison.Ordinal))
                .OrderByDescending(record => record.Timestamp).Select(record => record.Id),
            sut.SnapshotSelectedIds()
        );
        sut.LoadMore();
        Assert.All(sut.Groups.SelectMany(group => group.Entries), row => Assert.True(row.IsSelected));
        sut.ClearSelection();
        Assert.False(sut.HasSelection);
        Assert.All(sut.Groups.SelectMany(group => group.Entries), row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void ToggleSelecting_OffClearsSelection()
    {
        var history = CreateHistoryService();
        history.AddRecord(CreateRecord("selected"));
        var sut = CreateViewModel(history, CreateDictionaryService());
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        sut.ToggleSelectingCommand.Execute(null);
        row.IsSelected = true;
        Assert.True(row.IsSelecting);
        Assert.Equal(Loc.Instance["History.DoneSelecting"], sut.SelectionButtonText);
        Assert.Equal(Loc.Instance.GetString("History.SelectedCount", 1), sut.SelectionSummary);

        sut.ToggleSelectingCommand.Execute(null);

        Assert.False(sut.IsSelecting);
        Assert.False(row.IsSelecting);
        Assert.False(row.IsSelected);
        Assert.False(sut.HasSelection);
        Assert.Empty(sut.SnapshotSelectedIds());
        Assert.Equal(Loc.Instance["History.SelectEntries"], sut.SelectionButtonText);
    }

    [Fact]
    public async Task DeleteSelectedAsync_DeletesSnapshotStopsReadbackAndKeepsLaterEntries()
    {
        var history = CreateHistoryService();
        history.AddRecord(CreateRecord("read this"));
        var session = new ControlledPlaybackSession();
        using var speech = new SpeechFeedbackService(
            CreateSettingsService(),
            TestPluginManagerFactory.Create(),
            new ControlledTtsProvider(session)
        );
        var sut = CreateViewModel(history, CreateDictionaryService(), speech: speech);
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        sut.ToggleSelectingCommand.Execute(null);
        row.IsSelected = true;
        row.ToggleReadAloudCommand.Execute(null);
        await session.HandlerAttached.Task.WaitAsync(s_readbackGuard);
        var snapshot = sut.SnapshotSelectedIds();
        var later = CreateRecord("later");
        history.AddRecord(later);

        Assert.True(await sut.DeleteSelectedAsync(snapshot));

        Assert.False(row.IsReadingAloud);
        await session.StopCalled.Task.WaitAsync(s_readbackGuard);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(later.Id, Assert.Single(history.Records).Id);
        Assert.Equal(Loc.Instance.GetString("History.DeletedSelected", 1), sut.Notice);
        Assert.True(sut.HasNotice);
        Assert.False(sut.HasSelection);
    }

    [Fact]
    public async Task DeleteSelectedAsync_FailureKeepsSelectionAndShowsNotice()
    {
        var history = CreateHistoryService();
        var record = CreateRecord("selected");
        history.AddRecord(record);
        var sut = CreateViewModel(history, CreateDictionaryService());
        sut.ToggleSelectingCommand.Execute(null);
        sut.SelectAllShownCommand.Execute(null);
        var path = Path.Join(_tempDir, "history.json");
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(await sut.DeleteSelectedAsync(sut.SnapshotSelectedIds()));

        Assert.Equal(record.Id, Assert.Single(history.Records).Id);
        Assert.Equal(1, sut.SelectedCount);
        Assert.Equal(Loc.Instance["History.DeleteSelectedFailed"], sut.Notice);
        Assert.True(sut.HasNotice);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    [InlineData(".md")]
    [InlineData(".json")]
    public void BuildExportContent_UsesSelectionAndRejectsMissingIds(string extension)
    {
        var history = CreateHistoryService();
        var first = CreateRecord("first selected", timestamp: DateTime.UtcNow.AddMinutes(-2));
        var second = CreateRecord("second selected", timestamp: DateTime.UtcNow.AddMinutes(-1));
        history.AddRecord(first);
        history.AddRecord(second);
        history.AddRecord(CreateRecord("unselected"));
        var sut = CreateViewModel(history, CreateDictionaryService());
        string[] selected = [first.Id, second.Id, first.Id];
        Assert.Contains("unselected", sut.BuildExportContent(extension));
        Assert.Equal(sut.BuildExportContent(extension), sut.BuildExportContent(extension, []));
        sut.SearchQuery = "unselected";
        history.UpdateRecord(first.Id, "updated selected");

        var exported = sut.BuildExportContent(extension, selected);

        Assert.Contains("updated selected", exported);
        Assert.Contains("second selected", exported);
        Assert.DoesNotContain("unselected", exported);
        Assert.True(exported.IndexOf("second selected", StringComparison.Ordinal)
            < exported.IndexOf("updated selected", StringComparison.Ordinal));
        history.DeleteRecord(first.Id);
        var destination = Path.Join(_tempDir, "export" + extension);
        File.WriteAllText(destination, "existing export");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            File.WriteAllText(destination, sut.BuildExportContent(extension, selected)));
        Assert.Equal(Loc.Instance["History.ExportSelectionMissing"], exception.Message);
        Assert.Equal("existing export", File.ReadAllText(destination));
    }

    [Fact]
    public void SnapshotSelectedIds_KeepsEntriesHiddenByTheCurrentFilter()
    {
        var history = CreateHistoryService();
        var selected = CreateRecord("alpha selected");
        history.AddRecord(selected);
        history.AddRecord(CreateRecord("beta unselected"));
        var sut = CreateViewModel(history, CreateDictionaryService());
        sut.ToggleSelectingCommand.Execute(null);
        Assert.Single(sut.Groups.SelectMany(group => group.Entries), row => row.Record.Id == selected.Id)
            .IsSelected = true;

        sut.SearchQuery = "beta";

        Assert.Equal(1, sut.SelectedCount);
        Assert.True(sut.HasSelection);
        Assert.Equal([selected.Id], sut.SnapshotSelectedIds());
        var exported = sut.BuildExportContent(".txt", sut.SnapshotSelectedIds());
        Assert.Contains("alpha selected", exported);
        Assert.DoesNotContain("beta unselected", exported);
    }

    [Fact]
    public void BuildExportContent_ReportsOnlyTheRecordsTheExportWrites()
    {
        var history = CreateHistoryService();
        var succeeded = CreateRecord("succeeded entry");
        var failed = CreateRecord("failed entry") with
        {
            Status = TranscriptionRecordStatus.TranscriptionFailed,
        };
        history.AddRecord(succeeded);
        history.AddRecord(failed);
        var sut = CreateViewModel(history, CreateDictionaryService());

        var exported = sut.BuildExportContent(
            ".txt",
            [succeeded.Id, failed.Id],
            out var exportedCount
        );

        Assert.Equal(1, exportedCount);
        Assert.Contains("succeeded entry", exported);
        Assert.DoesNotContain("failed entry", exported);
    }

    private static void RefreshHistory(HistorySectionViewModel viewModel)
    {
        // These tests have no UI event loop to drain the refresh posted by RecordsChanged.
        typeof(HistorySectionViewModel).GetMethod(
            "Refresh",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        )!.Invoke(viewModel, null);
    }

    [Fact]
    public async Task ToggleReadAloud_StartsAndStopsRowReadback()
    {
        var history = CreateHistoryService();
        history.AddRecord(CreateRecord("read this") with { Language = "de" });
        var session = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(session);
        using var speech = new SpeechFeedbackService(
            CreateSettingsService(),
            TestPluginManagerFactory.Create(),
            provider
        );
        var sut = CreateViewModel(history, CreateDictionaryService(), speech: speech);
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.True(row.HasTranscript);
        Assert.Equal(Loc.Instance["History.ReadAloud"], row.ReadAloudButtonText);

        row.ToggleReadAloudCommand.Execute(null);
        await session.HandlerAttached.Task.WaitAsync(s_readbackGuard);

        Assert.True(row.IsReadingAloud);
        Assert.Equal(Loc.Instance["History.StopReading"], row.ReadAloudButtonText);
        Assert.Contains(nameof(row.IsReadingAloud), changed);
        Assert.Contains(nameof(row.ReadAloudButtonText), changed);
        Assert.Equal("de", Assert.Single(provider.Requests).Language);
        row.ToggleReadAloudCommand.Execute(null);

        Assert.False(row.IsReadingAloud);
        Assert.Equal(Loc.Instance["History.ReadAloud"], row.ReadAloudButtonText);
        await session.StopCalled.Task.WaitAsync(s_readbackGuard);
        Assert.Equal(1, session.StopCount);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("collapse")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("expand-other")]
    public async Task CollapseEditAndDelete_CancelOnlyThatRowsReadback(string action)
    {
        var history = CreateHistoryService();
        var record = CreateRecord("row A");
        history.AddRecord(record);
        var otherRecord = CreateRecord("row B");
        history.AddRecord(otherRecord);
        var firstSession = new ControlledPlaybackSession();
        var newerSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(firstSession, newerSession);
        using var speech = new SpeechFeedbackService(
            CreateSettingsService(),
            TestPluginManagerFactory.Create(),
            provider
        );
        var sut = CreateViewModel(history, CreateDictionaryService(), speech: speech);
        var rows = sut.Groups.SelectMany(group => group.Entries).ToArray();
        var row = rows.Single(entry => entry.Record.Id == record.Id);
        var other = rows.Single(entry => entry.Record.Id == otherRecord.Id);
        row.IsExpanded = true;
        row.ToggleReadAloudCommand.Execute(null);
        await firstSession.HandlerAttached.Task.WaitAsync(s_readbackGuard);
        sut.StopReadAloud(other);
        Assert.True(row.IsReadingAloud);

        switch (action)
        {
            case "collapse":
                row.IsExpanded = false;
                break;
            case "edit":
                row.StartEditCommand.Execute(null);
                break;
            case "delete":
                row.DeleteCommand.Execute(null);
                break;
            case "expand-other":
                other.IsExpanded = true;
                break;
        }

        Assert.False(row.IsReadingAloud);
        await firstSession.StopCalled.Task.WaitAsync(s_readbackGuard);
        Assert.Equal(1, firstSession.StopCount);
        speech.ReadBack("newer caller");
        sut.StopReadAloud(row);
        Assert.True(newerSession.IsActive);
        Assert.Equal(0, newerSession.StopCount);
    }

    [Fact]
    public async Task ClearAll_StopsRowReadback()
    {
        var history = CreateHistoryService();
        history.AddRecord(CreateRecord("read this"));
        var session = new ControlledPlaybackSession();
        using var speech = new SpeechFeedbackService(
            CreateSettingsService(),
            TestPluginManagerFactory.Create(),
            new ControlledTtsProvider(session)
        );
        var sut = CreateViewModel(history, CreateDictionaryService(), speech: speech);
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        row.ToggleReadAloudCommand.Execute(null);
        await session.HandlerAttached.Task.WaitAsync(s_readbackGuard);
        Assert.True(row.IsReadingAloud);

        sut.ClearAll();

        Assert.False(row.IsReadingAloud);
        await session.StopCalled.Task.WaitAsync(s_readbackGuard);
        Assert.Equal(1, session.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRowCleanup_DoesNotStopSupersedingSpeech(bool automatic)
    {
        var history = CreateHistoryService();
        history.AddRecord(CreateRecord("row A"));
        var session = new ControlledPlaybackSession();
        var newerSession = new ControlledPlaybackSession();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings { SpokenFeedbackEnabled = true });
        using var speech = new SpeechFeedbackService(
            settings.Object,
            TestPluginManagerFactory.Create(),
            new ControlledTtsProvider(session, newerSession)
        );
        var sut = CreateViewModel(history, CreateDictionaryService(), speech: speech);
        var row = Assert.Single(Assert.Single(sut.Groups).Entries);
        row.ToggleReadAloudCommand.Execute(null);
        await session.HandlerAttached.Task.WaitAsync(s_readbackGuard);
        if (automatic)
        {
            speech.SpeakAutomaticTranscription("newer transcription");
        }
        else
        {
            speech.StartReadBack("newer caller", null);
        }

        await newerSession.HandlerAttached.Task.WaitAsync(s_readbackGuard);
        sut.StopReadAloud(row);

        Assert.Equal(1, session.StopCount);
        Assert.True(newerSession.IsActive);
        Assert.Equal(0, newerSession.StopCount);
    }

    [Theory]
    [InlineData(TranscriptionRecordStatus.Succeeded, TextInsertionStatus.Pasted, true, true, false)]
    [InlineData(TranscriptionRecordStatus.Succeeded, TextInsertionStatus.Failed, true, true, true)]
    [InlineData(TranscriptionRecordStatus.TranscriptionFailed, TextInsertionStatus.Unknown, true, true, true)]
    [InlineData(TranscriptionRecordStatus.ProcessingFailed, TextInsertionStatus.Unknown, true, true, true)]
    [InlineData(TranscriptionRecordStatus.TranscriptionFailed, TextInsertionStatus.Unknown, false, true, false)]
    [InlineData(TranscriptionRecordStatus.TranscriptionFailed, TextInsertionStatus.Unknown, true, false, false)]
    public void RetryVisibility_RequiresFailureAudioAndRecoveryService(TranscriptionRecordStatus status,
        TextInsertionStatus insertion, bool audioExists, bool canRetry, bool visible)
    {
        var audio = new SessionAudioFileService(Path.Join(_tempDir, "audio"));
        var path = audioExists ? audio.SaveDictationCapture([1]) : "missing.wav";
        var record = CreateRecord("raw") with
        {
            Status = status, InsertionStatus = insertion, AudioFileName = Path.GetFileName(path),
            FailureMessage = "failure containing raw",
        };
        var owner = CreateViewModel(CreateHistoryService(), CreateDictionaryService(),
            retry: canRetry ? (_, _) => Task.FromResult(record) : null);
        var row = new HistoryRecordRow(record, owner);
        Assert.Equal(visible, row.ShowRetry);
        Assert.Equal(status != TranscriptionRecordStatus.Succeeded, row.HasFailure);
        Assert.Equal(row.HasFailure || visible, row.ShowRetryPanel);
        if (row.HasFailure)
            Assert.DoesNotContain("raw", row.FailureMessage);
    }

    [Fact]
    public void FailureMessage_BlankFailure_ShowsLocalizedFallback()
    {
        var record = CreateRecord("final") with
        {
            Status = TranscriptionRecordStatus.TranscriptionFailed, FailureMessage = "   ",
        };
        var row = new HistoryRecordRow(record, CreateViewModel(CreateHistoryService(), CreateDictionaryService()));

        Assert.Equal(Loc.Instance["Common.UnknownError"], row.FailureMessage);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("missing")]
    [InlineData("canceled")]
    public async Task Retry_ShowsProgressAndLocalizedResult(string outcome)
    {
        var record = CreateRecord("private raw text") with { Status = TranscriptionRecordStatus.ProcessingFailed };
        var completion = new TaskCompletionSource<TranscriptionRecord>();
        var owner = CreateViewModel(CreateHistoryService(), CreateDictionaryService(),
            retry: (_, _) => completion.Task);
        var row = new HistoryRecordRow(record, owner);
        var retry = row.RetryCommand.ExecuteAsync(null);
        Assert.True(row.IsRetrying);
        Assert.False(row.HasRetryResult);
        switch (outcome)
        {
            case "success":
                completion.SetResult(record with { Status = TranscriptionRecordStatus.Succeeded, FailureMessage = null });
                break;
            case "canceled":
                completion.SetException(new OperationCanceledException());
                break;
            case "missing":
                completion.SetException(new FileNotFoundException());
                break;
            default:
                completion.SetException(new InvalidOperationException("failure: private raw text"));
                break;
        }
        await retry;
        Assert.False(row.IsRetrying);
        Assert.True(row.HasRetryResult);
        Assert.Equal(outcome switch
        {
            "success" => Loc.Instance["History.RetryCopied"],
            "missing" => Loc.Instance["History.RetryAudioMissing"],
            "canceled" => Loc.Instance["History.RetryCanceled"],
            _ => Loc.Instance.GetString("History.RetryFailed", "failure: [redacted]"),
        }, row.RetryResult);
    }

    [Fact]
    public void SaveEdit_CreatesReviewableCorrectionSuggestion()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord("I use Kubernets daily.");
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary);
        var row = new HistoryRecordRow(record, sut);

        sut.SaveEdit(row, "I use Kubernetes daily.");

        var suggestion = Assert.Single(row.CorrectionSuggestions);
        Assert.True(suggestion.IsApproved);
        Assert.Equal("Kubernets", suggestion.Original);
        Assert.Equal("Kubernetes", suggestion.Replacement);
        Assert.Single(history.Records[0].PendingCorrectionSuggestions);
    }

    [Fact]
    public void SaveApprovedCorrections_LearnsApprovedSuggestionsOnly()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord("Use Kubernets and Postgres.");
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary);
        var row = new HistoryRecordRow(record, sut);

        sut.SaveEdit(row, "Use Kubernetes and Postgres.");
        row.SaveApprovedCorrectionsCommand.Execute(null);

        var entry = Assert.Single(dictionary.Entries);
        Assert.Equal(DictionaryEntryType.Correction, entry.EntryType);
        Assert.Equal(DictionaryEntrySource.CorrectionSuggestion, entry.Source);
        Assert.Equal("Kubernets", entry.Original);
        Assert.Equal("Kubernetes", entry.Replacement);
        Assert.Empty(row.CorrectionSuggestions);
        Assert.Empty(history.Records[0].PendingCorrectionSuggestions);
    }

    [Fact]
    public void AddToDictionary_AddsHistoryTextAsTerm()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord("Kubernetes");
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary);
        var row = new HistoryRecordRow(record, sut);

        row.AddToDictionaryCommand.Execute(null);

        var entry = Assert.Single(dictionary.Entries);
        Assert.Equal(DictionaryEntryType.Term, entry.EntryType);
        Assert.Equal("Kubernetes", entry.Original);
    }

    [Fact]
    public void SaveEdit_AutoLearnsCorrectionsWhenEnabled()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var settings = CreateSettingsService(true);
        var record = CreateRecord("I use Kubernets daily.");
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary, settings);
        var row = new HistoryRecordRow(record, sut);

        sut.SaveEdit(row, "I use Kubernetes daily.");

        Assert.Empty(row.CorrectionSuggestions);
        var entry = Assert.Single(dictionary.Entries);
        Assert.Equal("Kubernets", entry.Original);
        Assert.Equal("Kubernetes", entry.Replacement);
    }

    [Fact]
    public void Refresh_UtcPlus13_GroupsLateUtcRecordUnderLocalTodayAndFormatsLocalTime()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "History tests UTC+13",
            TimeSpan.FromHours(13),
            "History tests UTC+13",
            "History tests UTC+13"
        );
        var utcNow = new DateTime(2030, 1, 2, 23, 45, 0, DateTimeKind.Utc);
        var history = CreateHistoryService();
        var record = CreateRecord(
            "crosses midnight",
            timestamp: new DateTime(2030, 1, 2, 23, 30, 0, DateTimeKind.Utc)
        );
        history.AddRecord(record);

        var sut = CreateViewModel(
            history,
            CreateDictionaryService(),
            timeZone: timeZone,
            utcNow: () => utcNow
        );

        var group = Assert.Single(sut.Groups);
        Assert.Equal(Loc.Instance["History.GroupToday"], group.Name);
        var row = Assert.Single(group.Entries);
        Assert.Equal(new DateTime(2030, 1, 3, 12, 30, 0), row.LocalTimestamp);
        Assert.Equal("12:30", row.TimeLabel);
    }

    [Fact]
    public void Refresh_UtcMinus11_TreatsUnspecifiedTimestampAsUtcAndGroupsLocalYesterday()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "History tests UTC-11",
            TimeSpan.FromHours(-11),
            "History tests UTC-11",
            "History tests UTC-11"
        );
        var utcNow = new DateTime(2030, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var history = CreateHistoryService();
        var record = CreateRecord(
            "legacy timestamp",
            timestamp: new DateTime(2030, 1, 3, 10, 30, 0, DateTimeKind.Unspecified)
        );
        history.AddRecord(record);

        var sut = CreateViewModel(
            history,
            CreateDictionaryService(),
            timeZone: timeZone,
            utcNow: () => utcNow
        );

        var group = Assert.Single(sut.Groups);
        Assert.Equal(Loc.Instance["History.GroupYesterday"], group.Name);
        var row = Assert.Single(group.Entries);
        Assert.Equal(new DateTime(2030, 1, 2, 23, 30, 0), row.LocalTimestamp);
        Assert.Equal("23:30", row.TimeLabel);
    }

    private HistoryService CreateHistoryService()
    {
        return new HistoryService(Path.Join(_tempDir, "history.json"), Path.Join(_tempDir, "audio"));
    }

    private DictionaryService CreateDictionaryService()
    {
        return new DictionaryService(Path.Join(_tempDir, "dictionary.json"));
    }

    private SettingsService CreateSettingsService(
        bool autoAddCorrections = false,
        bool captureProvenance = false
    )
    {
        var settings = new SettingsService(
            Path.Join(_tempDir, $"settings-{Guid.NewGuid():N}.json")
        );
        settings.Save(
            AppSettings.Default with
            {
                AutoAddDictionaryCorrections = autoAddCorrections,
                CaptureLlmProvenance = captureProvenance,
            }
        );
        return settings;
    }

    private HistorySectionViewModel CreateViewModel(
        HistoryService history,
        DictionaryService dictionary,
        SettingsService? settings = null,
        TimeZoneInfo? timeZone = null,
        Func<DateTime>? utcNow = null,
        Func<string, CancellationToken, Task<TranscriptionRecord>>? retry = null,
        SpeechFeedbackService? speech = null
    ) =>
        new(
            history,
            dictionary,
            settings ?? CreateSettingsService(),
            new SessionAudioFileService(Path.Join(_tempDir, "audio")),
            // AudioPlaybackService opens audio hardware in its constructor — not
            // available in CI. GetUninitializedObject bypasses the constructor so
            // tests that never trigger audio playback don't fail on device init.
#pragma warning disable SYSLIB0050
            (AudioPlaybackService)
            FormatterServices.GetUninitializedObject(typeof(AudioPlaybackService)),
            timeZone ?? TimeZoneInfo.Local,
            utcNow ?? (() => DateTime.UtcNow),
            retry,
            speech
        );
#pragma warning restore SYSLIB0050

    private static TranscriptionRecord CreateRecord(
        string finalText,
        string? raw = null,
        IReadOnlyList<LlmCallProvenance>? llmCalls = null,
        DateTime? timestamp = null
    )
    {
        return new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = timestamp ?? DateTime.UtcNow,
            RawText = raw ?? finalText,
            FinalText = finalText,
            DurationSeconds = 2.4,
            AppProcessName = "test",
            LlmCalls = llmCalls ?? [],
        };
    }

    private static LlmCallProvenance CreateCall(
        string stage = "PromptAction",
        string providerName = "OpenAI",
        string modelId = "gpt-4",
        bool ranLocally = false,
        string? injectedContext = null
    )
    {
        return new LlmCallProvenance
        {
            Stage = stage,
            SystemPromptSent = "You are helpful.",
            UserPromptSent = "process this",
            ProviderName = providerName,
            ProviderId = "com.test.provider",
            ModelId = modelId,
            RanLocally = ranLocally,
            InjectedMemoryContext = injectedContext,
        };
    }

    [Fact]
    public void InspectorCalls_ProjectsProvenanceWithLabels()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord(
            "final text",
            raw: "raw text",
            llmCalls:
            [
                CreateCall(
                    "Cleanup",
                    injectedContext: "remembered fact"
                ),
            ]
        );
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary);
        var row = new HistoryRecordRow(record, sut);

        Assert.True(row.HasLlmCalls);
        var call = Assert.Single(row.InspectorCalls);
        Assert.Equal(Loc.Instance["History.Inspect.StageCleanup"], call.StageLabel);
        Assert.Equal("OpenAI · gpt-4", call.ProviderModelLabel);
        Assert.Equal("You are helpful.", call.SystemPromptSent);
        Assert.Equal("process this", call.UserPromptSent);
        Assert.Equal("remembered fact", call.InjectedMemoryContext);
        Assert.True(call.HasSystemPrompt);
        Assert.True(call.HasUserPrompt);
        Assert.True(call.HasInjectedContext);
    }

    [Fact]
    public void NetworkBadge_ReflectsLocalVsCloudProvider()
    {
        var cloud = new LlmCallDisplay(CreateCall(ranLocally: false, providerName: "OpenAI"));
        var local = new LlmCallDisplay(CreateCall(ranLocally: true, providerName: "Ollama"));

        Assert.Equal(
            Loc.Instance.GetString("History.Inspect.SentToProvider", "OpenAI"),
            cloud.NetworkBadgeText
        );
        Assert.False(cloud.RanLocally);
        Assert.Equal(Loc.Instance["History.Inspect.StayedLocal"], local.NetworkBadgeText);
        Assert.True(local.RanLocally);
    }

    [Fact]
    public void HasLlmCalls_FalseWhenEmpty_ShowRawVsFinalGating()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();

        // No LLM calls, raw == final: no inspector content at all.
        var plain = CreateRecord("same text");
        // No LLM calls but raw != final: diff-only inspector content.
        var diffOnly = CreateRecord("final text", raw: "raw text");
        history.AddRecord(plain);
        history.AddRecord(diffOnly);
        var sut = CreateViewModel(history, dictionary);

        var plainRow = new HistoryRecordRow(plain, sut);
        Assert.False(plainRow.HasLlmCalls);
        Assert.False(plainRow.ShowRawVsFinal);
        Assert.False(plainRow.HasInspectorContent);

        var diffRow = new HistoryRecordRow(diffOnly, sut);
        Assert.False(diffRow.HasLlmCalls);
        Assert.True(diffRow.ShowRawVsFinal);
        Assert.True(diffRow.HasInspectorContent);
    }

    [Fact]
    public void NoLlmCallsMessage_GuidesToSetting_WhenProvenanceCaptureOff()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord("final text", raw: "raw text"); // no LLM calls
        history.AddRecord(record);

        // Capture off: point the user at the setting instead of implying no LLM ran.
        var offRow = new HistoryRecordRow(
            record,
            CreateViewModel(history, dictionary, CreateSettingsService(captureProvenance: false))
        );
        Assert.Equal(Loc.Instance["History.Inspect.CaptureOff"], offRow.NoLlmCallsMessage);

        // Capture on: the entry genuinely made no LLM call.
        var onRow = new HistoryRecordRow(
            record,
            CreateViewModel(history, dictionary, CreateSettingsService(captureProvenance: true))
        );
        Assert.Equal(Loc.Instance["History.Inspect.NoLlmCalls"], onRow.NoLlmCallsMessage);
    }

    [Fact]
    public void ShowInspectorToggle_RequiresExpandedAndContent()
    {
        var history = CreateHistoryService();
        var dictionary = CreateDictionaryService();
        var record = CreateRecord("final", raw: "raw", llmCalls: [CreateCall()]);
        history.AddRecord(record);
        var sut = CreateViewModel(history, dictionary);
        var row = new HistoryRecordRow(record, sut);

        // Collapsed: toggle hidden even though there is content.
        Assert.False(row.ShowInspectorToggle);

        row.IsExpanded = true;
        Assert.True(row.ShowInspectorToggle);
        Assert.False(row.ShowInspector);

        row.ToggleInspectorCommand.Execute(null);
        Assert.True(row.ShowInspector);

        // Collapsing resets inspector visibility.
        row.IsExpanded = false;
        Assert.False(row.IsInspectorVisible);
        Assert.False(row.ShowInspector);
    }
}
