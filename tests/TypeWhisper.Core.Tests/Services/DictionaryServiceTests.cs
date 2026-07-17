using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

// The Assert.All lambdas in this file assert on each element; ReSharper reads xUnit
// asserts as precondition checks and concludes the element parameter is only
// validated, never used — but asserting on each element is exactly the test's
// purpose, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="DictionaryService" />: entries, term packs, corrections, CSV round-trip, and change notifications.</summary>
public sealed class DictionaryServiceTests : IDisposable
{
    private readonly string _filePath;
    private readonly DictionaryService _sut;

    public DictionaryServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new DictionaryService(_filePath);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public void AddEntry_AppearsInEntries()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );

        Assert.Single(_sut.Entries);
        Assert.Equal("React", _sut.Entries[0].Original);
    }

    [Fact]
    public void DeleteEntry_RemovesFromEntries()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );

        _sut.DeleteEntry("1");

        Assert.Empty(_sut.Entries);
    }

    [Fact]
    public void DeleteEntries_BatchRemove()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "A"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "2",
                EntryType = DictionaryEntryType.Term,
                Original = "B"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "3",
                EntryType = DictionaryEntryType.Term,
                Original = "C"
            }
        );

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
    public void ActivatePack_AllowsSameTermInDifferentSources()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "existing",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );

        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        Assert.Equal(3, _sut.Entries.Count);
        Assert.Contains(_sut.Entries, e => e is { Id: "existing", Original: "React" });
        Assert.Contains(_sut.Entries, e => e is { Id: "pack:test:React", Original: "React" });
    }

    [Fact]
    public void ApplyIndustryPreset_General_DoesNotActivateAnyPack()
    {
        _sut.ApplyIndustryPreset("general");

        Assert.Empty(_sut.Entries);
    }

    [Fact]
    public void ApplyIndustryPreset_UnknownId_DoesNotActivateAnyPack()
    {
        _sut.ApplyIndustryPreset("does-not-exist");

        Assert.Empty(_sut.Entries);
    }

    [Theory]
    [InlineData("real-estate")]
    [InlineData("architecture")]
    [InlineData("legal")]
    public void ApplyIndustryPreset_Industry_ActivatesMatchingPack(string presetId)
    {
        var preset = IndustryPreset.All.Single(p => p.Id == presetId);
        Assert.NotNull(preset.TermPackId);
        var pack = TermPack.FindById(preset.TermPackId!);
        Assert.NotNull(pack);

        _sut.ApplyIndustryPreset(presetId);

        Assert.Equal(pack.Terms.Length, _sut.Entries.Count);
        Assert.All(_sut.Entries, entry =>
        {
            Assert.Equal(DictionaryEntryType.Term, entry.EntryType);
            Assert.StartsWith($"pack:{preset.TermPackId}:", entry.Id);
        });
    }

    [Fact]
    public void DeactivatePack_RemovesPackTerms()
    {
        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "manual",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeScript"
            }
        );

        _sut.DeactivatePack("test");

        Assert.Single(_sut.Entries);
        Assert.Equal("TypeScript", _sut.Entries[0].Original);
    }

    [Fact]
    public void ApplyCorrections_ReplacesText()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes"
            }
        );

        var result = _sut.ApplyCorrections("I deployed to kubernets");
        Assert.Equal("I deployed to Kubernetes", result);
    }

    [Fact]
    public void PreviewCorrections_ReplacesText()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes"
            }
        );

        var result = _sut.PreviewCorrections("I deployed to kubernets");
        Assert.Equal("I deployed to Kubernetes", result);
    }

    [Fact]
    public void PreviewCorrections_DoesNotUpdateUsageMetadata()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes"
            }
        );

        _sut.PreviewCorrections("kubernets");
        _sut.PreviewCorrections("kubernets");
        _sut.PreviewCorrections("kubernets");

        var entry = _sut.Entries[0];
        Assert.Equal(0, entry.UsageCount);
        Assert.Equal(0, entry.TimesApplied);
        Assert.Null(entry.LastUsedAt);
    }

    [Fact]
    public void PreviewCorrections_DoesNotPersistAcrossInstances()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes"
            }
        );

        _sut.PreviewCorrections("kubernets");

        var reloadedService = new DictionaryService(_filePath);
        Assert.Equal(0, reloadedService.Entries[0].UsageCount);
    }

    [Fact]
    public void ApplyCorrections_UpdatesUsageMetadata()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes"
            }
        );

        _sut.ApplyCorrections("kubernets");

        var entry = _sut.Entries[0];
        Assert.Equal(1, entry.UsageCount);
        Assert.Equal(1, entry.TimesApplied);
        Assert.NotNull(entry.LastUsedAt);
    }

    [Fact]
    public void ApplyCorrections_DoesNotUpdateUsageMetadata_WhenWordBoundaryDoesNotMatch()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "test",
                Replacement = "exam"
            }
        );

        var result = _sut.ApplyCorrections("testing");

        Assert.Equal("testing", result);
        Assert.Equal(0, _sut.Entries[0].UsageCount);
        Assert.Equal(0, _sut.Entries[0].TimesApplied);
    }

    [Fact]
    public void ApplyCorrections_PrefersHigherPriorityCorrection()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "low",
                EntryType = DictionaryEntryType.Correction,
                Original = "type whisper",
                Replacement = "Type Whisper"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "high",
                EntryType = DictionaryEntryType.Correction,
                Original = "type whisper",
                Replacement = "TypeWhisper",
                Priority = 10
            }
        );

        var result = _sut.ApplyCorrections("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    [Fact]
    public void GetTermsForPrompt_ReturnsCommaSeparated()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "2",
                EntryType = DictionaryEntryType.Term,
                Original = "Vue"
            }
        );

        var result = _sut.GetTermsForPrompt();
        Assert.Equal("React, Vue", result);
    }

    [Fact]
    public void SetTerms_AppendsNormalizedTerms_WhenReplaceExistingFalse()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );

        _sut.SetTerms([" react ", "Vue", "", "vue"], false);

        Assert.Equal(["React", "Vue"], _sut.GetEnabledTerms());
    }

    [Fact]
    public void SetTerms_ReplacesExistingTerms_WhenReplaceExistingTrue()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "2",
                EntryType = DictionaryEntryType.Correction,
                Original = "teh",
                Replacement = "the"
            }
        );

        _sut.SetTerms(["Vue"], true);

        Assert.Equal(["Vue"], _sut.GetEnabledTerms());
        Assert.Contains(_sut.Entries, e => e.EntryType == DictionaryEntryType.Correction);
    }

    [Fact]
    public void RemoveAllTerms_KeepsCorrections()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "2",
                EntryType = DictionaryEntryType.Correction,
                Original = "teh",
                Replacement = "the"
            }
        );

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

    [Theory]
    [InlineData(DictionaryEntrySource.Manual)]
    [InlineData(DictionaryEntrySource.Import)]
    public void LearnCorrection_DoesNotOverwriteUserAuthoredEntry(DictionaryEntrySource source)
    {
        // A user-authored or imported mapping must never be silently replaced by one
        // observed target-app edit.
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "user-rule",
                EntryType = DictionaryEntryType.Correction,
                Original = "kubernets",
                Replacement = "Kubernetes",
                Source = source
            }
        );

        _sut.LearnCorrection("kubernets", "kubernetes cluster");

        var entry = Assert.Single(_sut.Entries);
        Assert.Equal("Kubernetes", entry.Replacement);
        Assert.Equal(source, entry.Source);
    }

    [Fact]
    public void LearnCorrections_AddsNewCorrectionsAsAutoLearnedAndReturnsIds()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("recieve", "receive")
        ]);

        Assert.Equal(2, learned.Count);
        Assert.Equal(2, _sut.Entries.Count);
        Assert.All(learned, c => Assert.NotEmpty(c.Id));
        Assert.All(
            _sut.Entries,
            e =>
            {
                Assert.Equal(DictionaryEntryType.Correction, e.EntryType);
                Assert.Equal(DictionaryEntrySource.AutoLearned, e.Source);
                Assert.Equal(1, e.TimesCorrected);
                Assert.NotNull(e.LastCorrectedAt);
            }
        );

        // Returned ids must be exactly the ids that were persisted.
        var learnedIds = learned.Select(c => c.Id).ToHashSet();
        Assert.Equal(learnedIds, _sut.Entries.Select(e => e.Id).ToHashSet());
    }

    [Theory]
    [InlineData(DictionaryEntrySource.Manual)]
    [InlineData(DictionaryEntrySource.Import)]
    [InlineData(DictionaryEntrySource.CorrectionSuggestion)]
    [InlineData(DictionaryEntrySource.AutoLearned)]
    public void LearnCorrections_NeverOverwritesExistingEntryOutsideReplaceableSet(
        DictionaryEntrySource source
    )
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "existing",
                EntryType = DictionaryEntryType.Correction,
                Original = "teh",
                Replacement = "the",
                Source = source
            }
        );

        var learned = _sut.LearnCorrections([new CorrectionSuggestion("teh", "different")]);

        Assert.Empty(learned);
        var entry = Assert.Single(_sut.Entries);
        Assert.Equal("the", entry.Replacement);
        Assert.Equal(source, entry.Source);
    }

    [Fact]
    public void LearnCorrections_UpdatesEntryWhenIdIsReplaceable()
    {
        var learned = _sut.LearnCorrections([new CorrectionSuggestion("teh", "the")]);
        var id = learned[0].Id;

        var relearned = _sut.LearnCorrections(
            [new CorrectionSuggestion("teh", "thee")],
            new HashSet<string> { id }
        );

        var updated = Assert.Single(relearned);
        Assert.Equal(id, updated.Id);
        Assert.Equal("thee", updated.Replacement);

        var entry = Assert.Single(_sut.Entries);
        Assert.Equal(id, entry.Id);
        Assert.Equal("thee", entry.Replacement);
        Assert.Equal(2, entry.TimesCorrected);
        Assert.Equal(DictionaryEntrySource.AutoLearned, entry.Source);
    }

    [Theory]
    [InlineData("foo!")]
    [InlineData("(bar")]
    [InlineData("")]
    public void LearnCorrections_RejectsUnsafeTokens(string original)
    {
        var learned = _sut.LearnCorrections([new CorrectionSuggestion(original, "safe")]);

        Assert.Empty(learned);
        Assert.Empty(_sut.Entries);
    }

    [Theory]
    [InlineData("its", "it's")]
    [InlineData("email", "e-mail")]
    [InlineData("kubernets", "Kubernetes")]
    public void LearnCorrections_AcceptsSafeTokens(string original, string replacement)
    {
        var learned = _sut.LearnCorrections([new CorrectionSuggestion(original, replacement)]);

        Assert.Single(learned);
        Assert.Single(_sut.Entries);
    }

    [Fact]
    public void LearnCorrections_WithinBatchDuplicateOriginals_FirstWins()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("TEH", "thee")
        ]);

        Assert.Single(learned);
        var entry = Assert.Single(_sut.Entries);
        Assert.Equal("teh", entry.Original);
        Assert.Equal("the", entry.Replacement);
    }

    [Fact]
    public void UndoLearnedCorrections_RemovesOnlyListedIdsAndLeavesTheRest()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("recieve", "receive")
        ]);
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "keep",
                EntryType = DictionaryEntryType.Correction,
                Original = "seperate",
                Replacement = "separate",
                Source = DictionaryEntrySource.Manual
            }
        );

        _sut.UndoLearnedCorrections([learned[0]]);

        Assert.Equal(2, _sut.Entries.Count);
        Assert.DoesNotContain(_sut.Entries, e => e.Id == learned[0].Id);
        Assert.Contains(_sut.Entries, e => e.Id == learned[1].Id);
        Assert.Contains(_sut.Entries, e => e.Id == "keep");
    }

    [Fact]
    public void Entries_LoadLegacyJsonWithMetadataDefaults()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "EntryType": 0,
                "Original": "React",
                "IsEnabled": true
              }
            ]
            """
        );

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
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Correction,
                Original = "wispr, flow",
                Replacement = "Wispr \"Flow\"",
                CaseSensitive = true,
                IsStarred = true,
                Priority = 7,
                Source = DictionaryEntrySource.CorrectionSuggestion
            }
        );

        var csv = _sut.ExportToCsv();

        Assert.Contains(
            "EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source",
            csv
        );
        Assert.Contains(
            "Correction,\"wispr, flow\",\"Wispr \"\"Flow\"\"\",True,True,True,7,CorrectionSuggestion",
            csv
        );
    }

    [Fact]
    public void ImportFromCsv_AddsEntriesWithMetadata()
    {
        var imported = _sut.ImportFromCsv(
            """
            EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source
            Correction,wispr,Wispr,true,true,true,5,Import
            Term,TypeWhisper,,false,true,false,2,Manual
            """
        );

        Assert.Equal(2, imported);
        Assert.Equal(2, _sut.Entries.Count);

        var correction = _sut.Entries.First(entry =>
            entry.EntryType == DictionaryEntryType.Correction
        );
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
    public void ImportFromCsv_UpdatesExistingCorrectionByOriginalIgnoringCase()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "existing-id",
                EntryType = DictionaryEntryType.Correction,
                Original = "wispr",
                Replacement = "Wispr",
                Priority = 5,
                Source = DictionaryEntrySource.Manual
            }
        );

        var imported = _sut.ImportFromCsv(
            """
            EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source
            Correction,WISPR,Wispr Flow,true,true,true,9,Import
            """
        );

        Assert.Equal(1, imported);
        var correction = Assert.Single(
            _sut.Entries,
            entry => entry.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("existing-id", correction.Id);
        Assert.Equal("Wispr Flow", correction.Replacement);
        Assert.Equal(9, correction.Priority);
    }

    [Fact]
    public void ImportFromCsv_ExactDuplicateCorrectionIsNoOp()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "existing-id",
                EntryType = DictionaryEntryType.Correction,
                Original = "wispr",
                Replacement = "Wispr",
                CaseSensitive = true,
                IsEnabled = false,
                IsStarred = true,
                UsageCount = 12,
                Priority = 5,
                Source = DictionaryEntrySource.Manual
            }
        );

        var imported = _sut.ImportFromCsv(
            """
            EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source
            Correction,WISPR,Wispr,true,false,true,5,Manual
            """
        );

        Assert.Equal(0, imported);
        var correction = Assert.Single(
            _sut.Entries,
            entry => entry.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("existing-id", correction.Id);
        Assert.Equal(12, correction.UsageCount);
    }

    [Fact]
    public void ImportFromCsv_LastCorrectionRowWinsForDuplicateOriginals()
    {
        var imported = _sut.ImportFromCsv(
            """
            EntryType,Original,Replacement
            Correction,wispr,First
            Correction,WISPR,Second
            """
        );

        Assert.Equal(2, imported);
        var correction = Assert.Single(
            _sut.Entries,
            entry => entry.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("Second", correction.Replacement);
    }

    [Fact]
    public void ImportFromCsv_SkipsDuplicatesAndInvalidCorrections()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "existing",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper"
            }
        );

        var imported = _sut.ImportFromCsv(
            """
            EntryType,Original,Replacement
            Term,TypeWhisper,
            Correction,wispr,
            Correction,wispr,Wispr
            """
        );

        Assert.Equal(1, imported);
        Assert.Equal(2, _sut.Entries.Count);
        var term = Assert.Single(
            _sut.Entries,
            entry => entry.EntryType == DictionaryEntryType.Term
        );
        Assert.Equal("existing", term.Id);
        Assert.Contains(
            _sut.Entries,
            entry =>
                entry is { EntryType: DictionaryEntryType.Correction, Replacement: "Wispr" }
        );
    }

    [Fact]
    public void UpdateEntry_ModifiesEntry()
    {
        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );

        _sut.UpdateEntry(_sut.Entries[0] with { Original = "React.js", CaseSensitive = true });

        Assert.Equal("React.js", _sut.Entries[0].Original);
        Assert.True(_sut.Entries[0].CaseSensitive);
    }

    [Fact]
    public void EntriesChanged_FiresOnModification()
    {
        var fired = 0;
        _sut.EntriesChanged += () => fired++;

        _sut.AddEntry(
            new DictionaryEntry
            {
                Id = "1",
                EntryType = DictionaryEntryType.Term,
                Original = "React"
            }
        );
        _sut.DeleteEntry("1");

        Assert.Equal(2, fired);
    }
}
