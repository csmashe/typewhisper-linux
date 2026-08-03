using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="VocabularyBoostingService" />: fuzzy rewriting of recognized text toward stored dictionary terms.</summary>
public class VocabularyBoostingServiceTests
{
    [Fact]
    public void Apply_ExactTermAlreadyPresent_LeavesTextUnchanged()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper",
            }
        );

        var result = sut.Apply("TypeWhisper is ready");

        Assert.Equal("TypeWhisper is ready", result);
    }

    [Fact]
    public void Apply_SingleWordTerm_RewritesSimilarRecognition()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet",
            }
        );

        var result = sut.Apply("parrakeet is loaded");

        Assert.Equal("Parakeet is loaded", result);
    }

    [Fact]
    public void Apply_MultiWordWindow_RewritesToStoredTerm()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper",
            }
        );

        var result = sut.Apply("type whisper for windows");

        Assert.Equal("TypeWhisper for windows", result);
    }

    [Fact]
    public void Apply_LowSimilarity_DoesNotRewrite()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet",
            }
        );

        var result = sut.Apply("papaya is loaded");

        Assert.Equal("papaya is loaded", result);
    }

    [Fact]
    public void Apply_AmbiguousMatchWithinMargin_DoesNotRewrite()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet",
            },
            new DictionaryEntry
            {
                Id = "manual-2",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeat",
            }
        );

        var result = sut.Apply("parakeit is loaded");

        Assert.Equal("parakeit is loaded", result);
    }

    [Fact]
    public void Apply_LongerTerm_WinsOverShorterOverlap()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Visual Studio",
            },
            new DictionaryEntry
            {
                Id = "manual-2",
                EntryType = DictionaryEntryType.Term,
                Original = "Studio",
            }
        );

        var result = sut.Apply("visual studeo project");

        Assert.Equal("Visual Studio project", result);
    }

    [Fact]
    public void Apply_ManualTerm_WinsOverPackVariant()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "pack:dotnet:typewhisper",
                EntryType = DictionaryEntryType.Term,
                Original = "typewhisper",
            },
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper",
            }
        );

        var result = sut.Apply("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    [Fact]
    public void Apply_DisabledTerms_AreIgnored()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper",
                IsEnabled = false,
            }
        );

        var result = sut.Apply("type whisper");

        Assert.Equal("type whisper", result);
    }

    [Fact]
    public void Apply_CorrectionEntries_AreIgnoredAsBoostSource()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Correction,
                Original = "type whisper",
                Replacement = "TypeWhisper",
            }
        );

        var result = sut.Apply("type whisper");

        Assert.Equal("type whisper", result);
    }

    [Fact]
    public void Apply_HyphenAndWhitespaceNormalization_RewritesToStoredForm()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Type-Whisper",
            }
        );

        var result = sut.Apply("type whisper");

        Assert.Equal("Type-Whisper", result);
    }

    [Fact]
    public void Apply_TermWithReplacement_UsesCanonicalReplacementAsOutput()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Type visped.",
                Replacement = "TypeWhisper",
            }
        );

        var result = sut.Apply("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    private static VocabularyBoostingService CreateSut(params DictionaryEntry[] entries)
    {
        return new VocabularyBoostingService(new FakeDictionaryService(entries));
    }

    private sealed class FakeDictionaryService : IDictionaryService
    {
        public FakeDictionaryService(IEnumerable<DictionaryEntry> entries)
        {
            Entries = entries.ToArray();
        }

        public IReadOnlyList<DictionaryEntry> Entries { get; }

        // Interface requires the event; this fake never raises it (the SUT only reads
        // Entries once at construction), so no-op accessors satisfy it without CS0067.
        public event Action? EntriesChanged { add { } remove { } }

        public void AddEntry(DictionaryEntry entry)
        {
            throw new NotSupportedException();
        }

        public void AddEntries(IEnumerable<DictionaryEntry> entries)
        {
            throw new NotSupportedException();
        }

        public void UpdateEntry(DictionaryEntry entry)
        {
            throw new NotSupportedException();
        }

        public void DeleteEntry(string id)
        {
            throw new NotSupportedException();
        }

        public void DeleteEntries(IEnumerable<string> ids)
        {
            throw new NotSupportedException();
        }

        public string ApplyCorrections(string text)
        {
            return text;
        }

        public string PreviewCorrections(string text)
        {
            return text;
        }

        public string? GetTermsForPrompt()
        {
            return null;
        }

        public void LearnCorrection(string original, string replacement)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(
            IEnumerable<CorrectionSuggestion> suggestions,
            IReadOnlySet<string>? replaceableEntryIds = null
        )
        {
            throw new NotSupportedException();
        }

        public void UndoLearnedCorrections(
            IEnumerable<LearnedDictionaryCorrection> learnedCorrections
        )
        {
            throw new NotSupportedException();
        }

        public void ActivatePack(TermPack pack)
        {
            throw new NotSupportedException();
        }

        public void DeactivatePack(string packId)
        {
            throw new NotSupportedException();
        }

        public void ApplyIndustryPreset(string presetId)
        {
            throw new NotSupportedException();
        }

        public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
        {
            throw new NotSupportedException();
        }

        public void RemoveAllTerms()
        {
            throw new NotSupportedException();
        }

        public bool DeleteTerm(string term)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<DictionaryCorrection> GetCorrections()
        {
            throw new NotSupportedException();
        }

        public DictionaryCorrection UpsertCorrection(
            string original,
            string replacement,
            bool caseSensitive
        )
        {
            throw new NotSupportedException();
        }

        public bool DeleteCorrection(string original)
        {
            throw new NotSupportedException();
        }

        public string ExportToCsv()
        {
            throw new NotSupportedException();
        }

        public int ImportFromCsv(string csv)
        {
            throw new NotSupportedException();
        }
    }
}
