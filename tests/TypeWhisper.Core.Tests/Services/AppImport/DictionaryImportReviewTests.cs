using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.AppImport;
using TypeWhisper.Tests;

// The Assert.All lambda asserts on each element; ReSharper reads xUnit asserts as precondition
// checks and concludes the element is only validated, never used, which is the test's purpose.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
namespace TypeWhisper.Core.Tests.Services.AppImport;

/// <summary>Covers <see cref="DictionaryImportReview" /> and <see cref="DictionaryService.CommitReviewedImport" />.</summary>
public sealed class DictionaryImportReviewTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateTempDirectory("dictionary-import");
    private readonly DictionaryService _dictionary;

    public DictionaryImportReviewTests()
    {
        _dictionary = new DictionaryService(Path.Join(_directory, "dictionary.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static DictionaryEntry Term(string original, bool enabled = true, string? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            EntryType = DictionaryEntryType.Term,
            Original = original,
            IsEnabled = enabled,
        };

    private static DictionaryEntry Correction(string original, string replacement, bool isRegex = false) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = DictionaryEntryType.Correction,
            Original = original,
            Replacement = replacement,
            IsRegex = isRegex,
        };

    private static DictionaryImportCandidate T(string original) => new(DictionaryEntryType.Term, original);

    private static DictionaryImportCandidate C(string original, string replacement) =>
        new(DictionaryEntryType.Correction, original, replacement);

    private DictionaryImportReview Review(params DictionaryImportCandidate[] candidates) =>
        DictionaryImportReview.For(new AppImportBatch(candidates), _dictionary.Entries, CancellationToken.None);

    [Fact]
    public void ClassifiesTermsWithoutWriting()
    {
        _dictionary.AddEntries([Term("TypeWhisper", id: "pack:test:TypeWhisper"), Term("Kubernetes", enabled: false)]);
        _dictionary.AddEntry(Correction("Avalonia", "Avalonia UI"));
        var before = File.ReadAllText(Path.Join(_directory, "dictionary.json"));

        var review = Review(T("typewhisper"), T("Kubernetes"), T("Avalonia"), T("New"), T(" new "), T(""), T("two\nlines"), T(new string('x', 161)));

        Assert.Equal(
            [
                // Term packs count like any other term.
                new DictionaryImportLine(DictionaryEntryType.Term, "typewhisper", null, DictionaryImportOutcome.Duplicate),
                new DictionaryImportLine(DictionaryEntryType.Term, "Kubernetes", null, DictionaryImportOutcome.Conflict),
                // A correction with the same text is a different kind of entry.
                new DictionaryImportLine(DictionaryEntryType.Term, "Avalonia", null, DictionaryImportOutcome.Add),
                new DictionaryImportLine(DictionaryEntryType.Term, "New", null, DictionaryImportOutcome.Add),
                new DictionaryImportLine(DictionaryEntryType.Term, "new", null, DictionaryImportOutcome.Duplicate),
                new DictionaryImportLine(DictionaryEntryType.Term, "", null, DictionaryImportOutcome.Unsupported),
                new DictionaryImportLine(DictionaryEntryType.Term, "two\nlines", null, DictionaryImportOutcome.Unsupported),
                new DictionaryImportLine(DictionaryEntryType.Term, new string('x', 161), null, DictionaryImportOutcome.Unsupported),
            ],
            review.Lines
        );
        Assert.Equal(["Avalonia", "New"], review.Additions.Select(entry => entry.Original));
        Assert.All(review.Additions, entry =>
        {
            Assert.Equal(DictionaryEntryType.Term, entry.EntryType);
            Assert.Equal(DictionaryEntrySource.Import, entry.Source);
            Assert.True(entry.IsEnabled);
        });
        Assert.Equal(before, File.ReadAllText(Path.Join(_directory, "dictionary.json")));
    }

    [Fact]
    public void ClassifiesCorrectionsAndKeepsConflictingOnes()
    {
        _dictionary.AddEntries([Correction("type whisper", "TypeWhisper"), Correction("teh", "tea"), Correction("\\s+", " ", isRegex: true)]);

        var review = Review(
            C("Type Whisper", "TypeWhisper"),
            C("teh", "the"),
            C("\\s+", " x"),
            C("new phrase", "New Phrase"),
            C("new phrase", "Other"),
            C("path", "C:\\new"),
            C("blank", "")
        );

        Assert.Equal(
            [
                DictionaryImportOutcome.Duplicate,
                DictionaryImportOutcome.Conflict,
                DictionaryImportOutcome.Conflict,
                DictionaryImportOutcome.Add,
                // Same original as the earlier addition with another spelling.
                DictionaryImportOutcome.Conflict,
                // A literal backslash spelling must not become a formatting escape.
                DictionaryImportOutcome.Unsupported,
                DictionaryImportOutcome.Unsupported,
            ],
            review.Lines.Select(line => line.Outcome)
        );
        var added = Assert.Single(review.Additions);
        Assert.Equal(("new phrase", "New Phrase", false), (added.Original, added.Replacement, added.ExpandEscapes));
    }

    [Fact]
    public void SourceExclusionsCountAsExcluded()
    {
        var review = DictionaryImportReview.For(
            new AppImportBatch([T("Alpha"), T("")], Excluded: 3),
            [],
            CancellationToken.None
        );

        Assert.Equal(4, review.Count(DictionaryImportOutcome.Unsupported));
        Assert.Equal(1, review.Count(DictionaryImportOutcome.Add));
    }

    [Fact]
    public void CommitAddsTheEntriesAndARepeatedImportAddsNothing()
    {
        _dictionary.AddEntries([Term("Kubernetes", enabled: false)]);
        var changes = 0;
        _dictionary.EntriesChanged += () => changes++;
        DictionaryImportCandidate[] batch = [T("Alpha"), T("TypeWhisper"), C("type whisper", "TypeWhisper"), T("Kubernetes")];

        Assert.Equal(DictionaryImportCommitResult.Imported, _dictionary.CommitReviewedImport(Review(batch)));
        Assert.Equal(1, changes);

        var repeat = Review(batch);
        Assert.Empty(repeat.Additions);
        Assert.Equal(DictionaryImportCommitResult.NothingToImport, _dictionary.CommitReviewedImport(repeat));

        var reloaded = new DictionaryService(Path.Join(_directory, "dictionary.json")).Entries;
        Assert.Equal(["Kubernetes", "Alpha", "TypeWhisper", "type whisper"], reloaded.Select(entry => entry.Original));
        Assert.False(reloaded[0].IsEnabled);
        Assert.Equal("TypeWhisper", reloaded[3].Replacement);
    }

    [Fact]
    public void ACommitIsRefusedWhenTheDictionaryChangedAfterTheReview()
    {
        var review = Review(T("Alpha"));
        _dictionary.AddEntry(Term("Edited meanwhile"));

        Assert.Equal(DictionaryImportCommitResult.Stale, _dictionary.CommitReviewedImport(review));
        Assert.Equal(["Edited meanwhile"], _dictionary.Entries.Select(entry => entry.Original));
    }

    [Fact]
    public void UsageCountersRecordedDuringTheReviewDoNotMakeItStale()
    {
        _dictionary.AddEntry(Correction("teh", "the"));
        var review = Review(T("Alpha"));

        Assert.Equal("the cat", _dictionary.ApplyCorrections("teh cat"));

        Assert.Equal(DictionaryImportCommitResult.Imported, _dictionary.CommitReviewedImport(review));
        Assert.Contains(_dictionary.Entries, entry => entry.Original == "Alpha");
    }

    [Fact]
    public void ASaveWhoseDirectorySyncFailsIsReportedAsNotDurableAndTheEntriesStayVisible()
    {
        var path = Path.Join(_directory, "unsynced.json");
        var dictionary = new DictionaryService(
            path,
            (file, json) =>
            {
                AtomicFileWrite.WriteAllText(file, json);
                throw new AtomicFileWriteIndeterminateCommitException(file, _directory, new IOException("fsync failed"));
            }
        );
        // A second service on the same file, already loaded, must not keep serving the old dictionary.
        var peer = new DictionaryService(path);
        Assert.Empty(peer.Entries);
        var changes = 0;
        dictionary.EntriesChanged += () => changes++;
        var review = DictionaryImportReview.For(new AppImportBatch([T("Alpha")]), dictionary.Entries, CancellationToken.None);

        Assert.Equal(DictionaryImportCommitResult.ImportedNotDurable, dictionary.CommitReviewedImport(review));

        Assert.Equal(1, changes);
        Assert.Equal(["Alpha"], dictionary.Entries.Select(entry => entry.Original));
        Assert.Equal(["Alpha"], peer.Entries.Select(entry => entry.Original));
    }

    [Fact]
    public void ReviewHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DictionaryImportReview.For(new AppImportBatch([T("Alpha")]), [], cancellation.Token)
        );
    }
}
