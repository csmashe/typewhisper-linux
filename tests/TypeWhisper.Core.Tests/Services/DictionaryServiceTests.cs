using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class DictionaryServiceTests : IDisposable
{
    private readonly string _filePath;
    private readonly DictionaryService _sut;

    public DictionaryServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new DictionaryService(_filePath);
    }

    [Fact]
    public void AddEntry_AppearsInEntries()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        Assert.Single(_sut.Entries);
        Assert.Equal("React", _sut.Entries[0].Original);
    }

    [Fact]
    public void DeleteEntry_RemovesFromEntries()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        _sut.DeleteEntry("1");

        Assert.Empty(_sut.Entries);
    }

    [Fact]
    public void DeleteEntries_BatchRemove()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "A" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Term, Original = "B" });
        _sut.AddEntry(new DictionaryEntry { Id = "3", EntryType = DictionaryEntryType.Term, Original = "C" });

        _sut.DeleteEntries(["1", "3"]);

        Assert.Single(_sut.Entries);
        Assert.Equal("B", _sut.Entries[0].Original);
    }

    [Fact]
    public void ActivatePack_InsertsTerms()
    {
        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue", "Angular"]);

        _sut.ActivatePack(pack);

        Assert.Equal(3, _sut.Entries.Count);
        Assert.All(_sut.Entries, e => Assert.Equal(DictionaryEntryType.Term, e.EntryType));
        Assert.All(_sut.Entries, e => Assert.StartsWith("pack:test:", e.Id));
    }

    [Fact]
    public void ActivatePack_SkipsDuplicates()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "existing",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        // Should have 2 entries: the existing "React" and the new "Vue"
        Assert.Equal(2, _sut.Entries.Count);
    }

    [Fact]
    public void DeactivatePack_RemovesPackTerms()
    {
        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        // Add a manual entry that shouldn't be removed
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "manual",
            EntryType = DictionaryEntryType.Term,
            Original = "TypeScript"
        });

        _sut.DeactivatePack("test");

        Assert.Single(_sut.Entries);
        Assert.Equal("TypeScript", _sut.Entries[0].Original);
    }

    [Fact]
    public void ApplyCorrections_ReplacesText()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "kubernets",
            Replacement = "Kubernetes"
        });

        var result = _sut.ApplyCorrections("I deployed to kubernets");
        Assert.Equal("I deployed to Kubernetes", result);
    }

    [Fact]
    public void ApplyCorrections_UpdatesUsageMetadata()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "kubernets",
            Replacement = "Kubernetes"
        });

        _sut.ApplyCorrections("kubernets");

        var entry = _sut.Entries[0];
        Assert.Equal(1, entry.UsageCount);
        Assert.Equal(1, entry.TimesApplied);
        Assert.NotNull(entry.LastUsedAt);
    }

    [Fact]
    public void ApplyCorrections_DoesNotUpdateUsageMetadata_WhenWordBoundaryDoesNotMatch()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "test",
            Replacement = "exam"
        });

        var result = _sut.ApplyCorrections("testing");

        Assert.Equal("testing", result);
        Assert.Equal(0, _sut.Entries[0].UsageCount);
        Assert.Equal(0, _sut.Entries[0].TimesApplied);
    }

    [Fact]
    public void ApplyCorrections_PrefersHigherPriorityCorrection()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "low",
            EntryType = DictionaryEntryType.Correction,
            Original = "type whisper",
            Replacement = "Type Whisper"
        });
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "high",
            EntryType = DictionaryEntryType.Correction,
            Original = "type whisper",
            Replacement = "TypeWhisper",
            Priority = 10
        });

        var result = _sut.ApplyCorrections("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    [Fact]
    public void GetTermsForPrompt_ReturnsCommaSeparated()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Term, Original = "Vue" });

        var result = _sut.GetTermsForPrompt();
        Assert.Equal("React, Vue", result);
    }

    [Fact]
    public void SetTerms_AppendsNormalizedTerms_WhenReplaceExistingFalse()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });

        _sut.SetTerms([" react ", "Vue", "", "vue"], replaceExisting: false);

        Assert.Equal(["React", "Vue"], _sut.GetEnabledTerms());
    }

    [Fact]
    public void SetTerms_ReplacesExistingTerms_WhenReplaceExistingTrue()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Correction, Original = "teh", Replacement = "the" });

        _sut.SetTerms(["Vue"], replaceExisting: true);

        Assert.Equal(["Vue"], _sut.GetEnabledTerms());
        Assert.Contains(_sut.Entries, e => e.EntryType == DictionaryEntryType.Correction);
    }

    [Fact]
    public void RemoveAllTerms_KeepsCorrections()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Correction, Original = "teh", Replacement = "the" });

        _sut.RemoveAllTerms();

        Assert.Empty(_sut.GetEnabledTerms());
        Assert.Single(_sut.Entries);
        Assert.Equal(DictionaryEntryType.Correction, _sut.Entries[0].EntryType);
    }

    [Fact]
    public void GetTermsForPrompt_ReturnsNull_WhenNoTerms()
    {
        Assert.Null(_sut.GetTermsForPrompt());
    }

    [Fact]
    public void LearnCorrection_AddsNewCorrection()
    {
        _sut.LearnCorrection("kubernets", "Kubernetes");

        Assert.Single(_sut.Entries);
        Assert.Equal(DictionaryEntryType.Correction, _sut.Entries[0].EntryType);
        Assert.Equal("kubernets", _sut.Entries[0].Original);
        Assert.Equal("Kubernetes", _sut.Entries[0].Replacement);
        Assert.Equal(1, _sut.Entries[0].TimesCorrected);
        Assert.NotNull(_sut.Entries[0].LastCorrectedAt);
        Assert.Equal(DictionaryEntrySource.CorrectionSuggestion, _sut.Entries[0].Source);
    }

    [Fact]
    public void LearnCorrection_UpdatesExisting()
    {
        _sut.LearnCorrection("kubernets", "Kubernets");
        _sut.LearnCorrection("kubernets", "Kubernetes");

        Assert.Single(_sut.Entries);
        Assert.Equal("Kubernetes", _sut.Entries[0].Replacement);
        Assert.Equal(1, _sut.Entries[0].UsageCount);
        Assert.Equal(2, _sut.Entries[0].TimesCorrected);
        Assert.NotNull(_sut.Entries[0].LastCorrectedAt);
    }

    [Fact]
    public void Entries_LoadLegacyJsonWithMetadataDefaults()
    {
        File.WriteAllText(_filePath, """
            [
              {
                "Id": "legacy",
                "EntryType": 0,
                "Original": "React",
                "IsEnabled": true
              }
            ]
            """);

        var sut = new DictionaryService(_filePath);

        var entry = Assert.Single(sut.Entries);
        Assert.Equal("React", entry.Original);
        Assert.False(entry.IsStarred);
        Assert.Equal(0, entry.TimesApplied);
        Assert.Equal(0, entry.TimesCorrected);
        Assert.Equal(0, entry.Priority);
        Assert.Null(entry.LastUsedAt);
        Assert.Null(entry.LastCorrectedAt);
        Assert.Equal(DictionaryEntrySource.Manual, entry.Source);
    }

    [Fact]
    public void ExportToCsv_IncludesMetadataAndEscapesFields()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "wispr, flow",
            Replacement = "Wispr \"Flow\"",
            CaseSensitive = true,
            IsStarred = true,
            Priority = 7,
            Source = DictionaryEntrySource.CorrectionSuggestion
        });

        var csv = _sut.ExportToCsv();

        Assert.Contains("EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source", csv);
        Assert.Contains("Correction,\"wispr, flow\",\"Wispr \"\"Flow\"\"\",True,True,True,7,CorrectionSuggestion", csv);
    }

    [Fact]
    public void ImportFromCsv_AddsEntriesWithMetadata()
    {
        var imported = _sut.ImportFromCsv("""
            EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source
            Correction,wispr,Wispr,true,true,true,5,Import
            Term,TypeWhisper,,false,true,false,2,Manual
            """);

        Assert.Equal(2, imported);
        Assert.Equal(2, _sut.Entries.Count);

        var correction = _sut.Entries.First(entry => entry.EntryType == DictionaryEntryType.Correction);
        Assert.Equal("wispr", correction.Original);
        Assert.Equal("Wispr", correction.Replacement);
        Assert.True(correction.CaseSensitive);
        Assert.True(correction.IsStarred);
        Assert.Equal(5, correction.Priority);
        Assert.Equal(DictionaryEntrySource.Import, correction.Source);

        var term = _sut.Entries.First(entry => entry.EntryType == DictionaryEntryType.Term);
        Assert.Equal("TypeWhisper", term.Original);
        Assert.Null(term.Replacement);
        Assert.Equal(2, term.Priority);
        Assert.Equal(DictionaryEntrySource.Manual, term.Source);
    }

    [Fact]
    public void ImportFromCsv_SkipsDuplicatesAndInvalidCorrections()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "existing",
            EntryType = DictionaryEntryType.Term,
            Original = "TypeWhisper"
        });

        var imported = _sut.ImportFromCsv("""
            EntryType,Original,Replacement
            Term,TypeWhisper,
            Correction,wispr,
            Correction,wispr,Wispr
            """);

        Assert.Equal(1, imported);
        Assert.Equal(2, _sut.Entries.Count);
        Assert.Contains(_sut.Entries, entry => entry.EntryType == DictionaryEntryType.Correction && entry.Replacement == "Wispr");
    }

    [Fact]
    public void UpdateEntry_ModifiesEntry()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        _sut.UpdateEntry(_sut.Entries[0] with { Original = "React.js", CaseSensitive = true });

        Assert.Equal("React.js", _sut.Entries[0].Original);
        Assert.True(_sut.Entries[0].CaseSensitive);
    }

    [Fact]
    public void EntriesChanged_FiresOnModification()
    {
        var fired = 0;
        _sut.EntriesChanged += () => fired++;

        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.DeleteEntry("1");

        Assert.Equal(2, fired);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
