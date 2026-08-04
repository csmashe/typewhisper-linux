using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers the corrections/terms surface of <see cref="DictionaryService" /> (upsert, delete, enabled-only filtering).</summary>
public sealed class DictionaryServiceCorrectionsTests : IDisposable
{
    private readonly string _filePath;
    private readonly DictionaryService _sut;

    public DictionaryServiceCorrectionsTests()
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
    public void GetCorrections_ReturnsEnabledOnly()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "teh",
            Replacement = "the",
            IsEnabled = true,
        });
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "2",
            EntryType = DictionaryEntryType.Correction,
            Original = "recieve",
            Replacement = "receive",
            IsEnabled = false,
        });
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "3",
            EntryType = DictionaryEntryType.Term,
            Original = "React",
        });

        var corrections = _sut.GetCorrections();

        Assert.Single(corrections);
        Assert.Equal("teh", corrections[0].Original);
        Assert.Equal("the", corrections[0].Replacement);
    }

    [Fact]
    public void UpsertCorrection_Insert()
    {
        var added = _sut.UpsertCorrection("teh", "the", caseSensitive: false);

        Assert.Equal("teh", added.Original);
        Assert.Equal("the", added.Replacement);
        Assert.Single(_sut.GetCorrections());
    }

    [Fact]
    public void UpsertCorrection_UpdatesExistingCaseInsensitive()
    {
        _sut.UpsertCorrection("teh", "the", caseSensitive: false);
        _sut.UpsertCorrection("TEH", "THE", caseSensitive: true);

        var corrections = _sut.GetCorrections();
        Assert.Single(corrections);
        Assert.Equal("THE", corrections[0].Replacement);
        Assert.True(corrections[0].CaseSensitive);
    }

    [Fact]
    public void UpsertCorrection_IdenticalReupsert_DoesNotRaiseEntriesChanged()
    {
        _sut.UpsertCorrection("teh", "the", caseSensitive: false);
        var changes = 0;
        _sut.EntriesChanged += () => changes++;

        _sut.UpsertCorrection("teh", "the", caseSensitive: false);
        Assert.Equal(0, changes);

        _sut.UpsertCorrection("teh", "THE", caseSensitive: false);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DeleteCorrection_Match()
    {
        _sut.UpsertCorrection("teh", "the", false);

        var deleted = _sut.DeleteCorrection("teh");

        Assert.True(deleted);
        Assert.Empty(_sut.GetCorrections());
    }

    [Fact]
    public void DeleteCorrection_NoMatch()
    {
        _sut.UpsertCorrection("teh", "the", false);

        var deleted = _sut.DeleteCorrection("xyz");

        Assert.False(deleted);
        Assert.Single(_sut.GetCorrections());
    }

    [Fact]
    public void DeleteTerm_Match()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "FooCorp",
        });

        var deleted = _sut.DeleteTerm("foocorp");

        Assert.True(deleted);
        Assert.Empty(_sut.GetEnabledTerms());
    }

    [Fact]
    public void DeleteTerm_NoMatch_ReturnsFalse()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "FooCorp",
        });

        var deleted = _sut.DeleteTerm("BarCorp");

        Assert.False(deleted);
        Assert.Single(_sut.GetEnabledTerms());
    }

    [Fact]
    public void DeleteTerm_LeavesCorrectionsAlone()
    {
        _sut.UpsertCorrection("teh", "the", false);
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "teh",
        });

        var deleted = _sut.DeleteTerm("teh");

        Assert.True(deleted);
        Assert.Single(_sut.GetCorrections());
    }
}
