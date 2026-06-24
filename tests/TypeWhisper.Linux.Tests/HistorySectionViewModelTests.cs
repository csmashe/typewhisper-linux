using System.Runtime.Serialization;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HistorySectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public HistorySectionViewModelTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "TypeWhisper.History.Tests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
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

    private HistoryService CreateHistoryService()
    {
        return new HistoryService(Path.Join(_tempDir, "history.json"), Path.Join(_tempDir, "audio"));
    }

    private DictionaryService CreateDictionaryService()
    {
        return new DictionaryService(Path.Join(_tempDir, "dictionary.json"));
    }

    private SettingsService CreateSettingsService(bool autoAddCorrections = false)
    {
        var settings = new SettingsService(
            Path.Join(_tempDir, $"settings-{Guid.NewGuid():N}.json")
        );
        settings.Save(
            AppSettings.Default with
            {
                AutoAddDictionaryCorrections = autoAddCorrections
            }
        );
        return settings;
    }

    private HistorySectionViewModel CreateViewModel(
        HistoryService history,
        DictionaryService dictionary,
        SettingsService? settings = null
    ) =>
        new(
            history,
            dictionary,
            settings ?? CreateSettingsService(),
            new CorrectionSuggestionService(),
            new SessionAudioFileService(),
            // AudioPlaybackService opens audio hardware in its constructor — not
            // available in CI. GetUninitializedObject bypasses the constructor so
            // tests that never trigger audio playback don't fail on device init.
#pragma warning disable SYSLIB0050
            (AudioPlaybackService)
            FormatterServices.GetUninitializedObject(typeof(AudioPlaybackService))
        );
#pragma warning restore SYSLIB0050

    private static TranscriptionRecord CreateRecord(string finalText)
    {
        return new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            RawText = finalText,
            FinalText = finalText,
            DurationSeconds = 2.4,
            AppProcessName = "test"
        };
    }
}