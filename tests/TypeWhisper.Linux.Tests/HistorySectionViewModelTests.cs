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
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.History.Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        Assert.Single(history.Records.First().PendingCorrectionSuggestions);
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
        Assert.Empty(history.Records.First().PendingCorrectionSuggestions);
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
        var settings = CreateSettingsService(autoAddCorrections: true);
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

    private HistoryService CreateHistoryService() =>
        new(Path.Combine(_tempDir, "history.json"), Path.Combine(_tempDir, "audio"));

    private DictionaryService CreateDictionaryService() =>
        new(Path.Combine(_tempDir, "dictionary.json"));

    private SettingsService CreateSettingsService(bool autoAddCorrections = false)
    {
        var settings = new SettingsService(Path.Combine(_tempDir, $"settings-{Guid.NewGuid():N}.json"));
        settings.Save(AppSettings.Default with { AutoAddDictionaryCorrections = autoAddCorrections });
        return settings;
    }

    private HistorySectionViewModel CreateViewModel(
        HistoryService history,
        DictionaryService dictionary,
        SettingsService? settings = null) =>
        new(
            history,
            dictionary,
            settings ?? CreateSettingsService(),
            new CorrectionSuggestionService(),
            new SessionAudioFileService(),
#pragma warning disable SYSLIB0050
            (AudioPlaybackService)FormatterServices.GetUninitializedObject(typeof(AudioPlaybackService)));
#pragma warning restore SYSLIB0050

    private static TranscriptionRecord CreateRecord(string finalText) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            RawText = finalText,
            FinalText = finalText,
            DurationSeconds = 2.4,
            AppProcessName = "test"
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }
}
