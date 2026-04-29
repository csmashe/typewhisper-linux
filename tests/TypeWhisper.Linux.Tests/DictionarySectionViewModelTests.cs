using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictionarySectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public DictionarySectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Dictionary.Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void AddEntry_PersistsPriority()
    {
        var dictionary = CreateDictionaryService();
        var sut = CreateViewModel(dictionary);

        sut.NewOriginal = "type whisper";
        sut.NewReplacement = "TypeWhisper";
        sut.NewPriority = 4;
        sut.AddEntryCommand.Execute(null);

        var entry = Assert.Single(dictionary.Entries);
        Assert.Equal(4, entry.Priority);
        Assert.Equal(0, sut.NewPriority);
    }

    [Fact]
    public void EntryControls_UpdateStarredAndPriority()
    {
        var dictionary = CreateDictionaryService();
        var entry = new DictionaryEntry
        {
            Id = "entry-1",
            EntryType = DictionaryEntryType.Correction,
            Original = "wispr",
            Replacement = "Wispr",
            Priority = 1
        };
        dictionary.AddEntry(entry);
        var sut = CreateViewModel(dictionary);

        sut.ToggleStarredCommand.Execute(entry);
        var updated = dictionary.Entries.Single();
        Assert.True(updated.IsStarred);

        sut.IncreasePriorityCommand.Execute(updated);
        updated = dictionary.Entries.Single();
        Assert.Equal(2, updated.Priority);

        sut.DecreasePriorityCommand.Execute(updated);
        updated = dictionary.Entries.Single();
        Assert.Equal(1, updated.Priority);
    }

    [Fact]
    public void Refresh_SortsStarredAndHighPriorityFirst()
    {
        var dictionary = CreateDictionaryService();
        dictionary.AddEntries(
        [
            new DictionaryEntry
            {
                Id = "low",
                EntryType = DictionaryEntryType.Term,
                Original = "alpha"
            },
            new DictionaryEntry
            {
                Id = "priority",
                EntryType = DictionaryEntryType.Term,
                Original = "beta",
                Priority = 5
            },
            new DictionaryEntry
            {
                Id = "starred",
                EntryType = DictionaryEntryType.Term,
                Original = "gamma",
                IsStarred = true
            }
        ]);

        var sut = CreateViewModel(dictionary);

        Assert.Equal(["gamma", "beta", "alpha"], sut.FilteredEntries.Select(entry => entry.Original));
    }

    private DictionaryService CreateDictionaryService() =>
        new(Path.Combine(_tempDir, "dictionary.json"));

    private DictionarySectionViewModel CreateViewModel(DictionaryService dictionary) =>
        new(dictionary, new SettingsService(Path.Combine(_tempDir, "settings.json")));

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
