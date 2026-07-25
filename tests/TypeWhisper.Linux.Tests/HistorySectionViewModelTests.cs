using System.Runtime.Serialization;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HistorySectionViewModelTests : IDisposable
{
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
        Func<DateTime>? utcNow = null
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
            utcNow ?? (() => DateTime.UtcNow)
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
