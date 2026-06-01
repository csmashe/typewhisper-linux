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
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "TypeWhisper.Dictionary.Tests_" + Guid.NewGuid().ToString("N")
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
        dictionary.AddEntries([
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

        Assert.Equal(
            ["gamma", "beta", "alpha"],
            sut.FilteredEntries.Select(entry => entry.Original)
        );
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_PicksUpExternallySavedEnabledPackId()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DictionarySectionViewModel(dictionary, settings);
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.False(realEstatePack.IsEnabled);
        Assert.Empty(dictionary.Entries);

        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        sut.ReconcileEnabledPacksFromSettings();

        Assert.True(realEstatePack.IsEnabled);
        Assert.NotEmpty(dictionary.Entries);
        Assert.All(dictionary.Entries, e => Assert.StartsWith("pack:real-estate:", e.Id));
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_IsNoOpWhenAlreadyInSync()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        var sut = new DictionarySectionViewModel(dictionary, settings);
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.True(realEstatePack.IsEnabled);

        sut.ReconcileEnabledPacksFromSettings();

        Assert.True(realEstatePack.IsEnabled);
    }

    [Fact]
    public void ReconcileEnabledPacksFromSettings_DeactivatesPackTermsWhenRemovedFromSettings()
    {
        var dictionary = CreateDictionaryService();
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DictionarySectionViewModel(dictionary, settings);
        settings.Save(settings.Current with { EnabledPackIds = ["real-estate"] });
        sut.ReconcileEnabledPacksFromSettings();
        var realEstatePack = sut.Packs.Single(p => p.Pack.Id == "real-estate");
        Assert.True(realEstatePack.IsEnabled);
        Assert.NotEmpty(dictionary.Entries);

        settings.Save(settings.Current with { EnabledPackIds = [] });
        sut.ReconcileEnabledPacksFromSettings();

        Assert.False(realEstatePack.IsEnabled);
        Assert.Empty(dictionary.Entries);
    }

    private DictionaryService CreateDictionaryService()
    {
        return new DictionaryService(Path.Combine(_tempDir, "dictionary.json"));
    }

    private DictionarySectionViewModel CreateViewModel(DictionaryService dictionary)
    {
        return new DictionarySectionViewModel(dictionary, new SettingsService(Path.Combine(_tempDir, "settings.json")));
    }
}