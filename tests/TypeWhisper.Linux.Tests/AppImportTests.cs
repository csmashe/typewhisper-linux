using System.Text;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.AppImport;
using TypeWhisper.Linux.Services.AppImport;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the Handy and Wispr Flow import review dialog's view model and Handy's default settings path.
///     The env var is mutated in place, safe only because the assembly disables test parallelization.
/// </summary>
public sealed class AppImportTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateTempDirectory("app-import");
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly DictionaryService _dictionary;

    public AppImportTests()
    {
        _dictionary = new DictionaryService(Path.Join(_directory, "dictionary.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        Directory.Delete(_directory, recursive: true);
    }

    private static AppImportSource HandyWords(params string[] words)
    {
        var json = "{\"settings\":{\"custom_words\":[" + string.Join(",", words.Select(w => $"\"{w}\"")) + "]}}";
        return AppImportSource.Handy((_, _) => Encoding.UTF8.GetBytes(json));
    }

    private static AppImportSource Failing(AppImportReadFailure failure) =>
        AppImportSource.WisprFlow with { Load = (_, _) => throw new AppImportReadException(failure, "test") };

    private AppImportViewModel Import(AppImportSource source, IDictionaryService? dictionary = null) =>
        new(dictionary ?? _dictionary, source, "source-file");

    [Fact]
    public void HandysDefaultPathIsInItsXdgDataDirectory()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/custom/data");

        Assert.Equal("/custom/data/com.pais.handy/settings_store.json", AppImportSource.HandyDefaultSettingsPath());
    }

    [Fact]
    public async Task ShowsTheHandyReviewAndAddsOnlyTheNewTermsOnCommit()
    {
        _dictionary.AddEntry(new DictionaryEntry { Id = "existing", EntryType = DictionaryEntryType.Term, Original = "TypeWhisper" });
        using var import = Import(HandyWords("typewhisper", "Avalonia", ""));

        await import.LoadAsync();

        Assert.False(import.IsLoading);
        Assert.False(import.HasMessage);
        Assert.Equal("Import from Handy", import.Title);
        Assert.True(import.CanAdd);
        Assert.Equal("Add to dictionary (1)", import.AddButtonText);
        Assert.Equal("1 new · 1 already present · 0 conflicts kept unchanged · 1 excluded", import.Summary);
        Assert.Equal(
            [("typewhisper", "Already present"), ("Avalonia", "Add"), ("(blank)", "Excluded")],
            import.Lines.Select(line => (line.Entry, line.OutcomeLabel))
        );
        Assert.Single(_dictionary.Entries);

        Assert.Equal("Dictionary entries added from Handy: 1.", import.Commit());
        Assert.Equal(["TypeWhisper", "Avalonia"], _dictionary.Entries.Select(entry => entry.Original));
        Assert.False(import.CanAdd);
    }

    [Fact]
    public async Task ShowsWisprCorrectionsAndCountsSourceExclusions()
    {
        var source = AppImportSource.WisprFlow with
        {
            Load = (_, _) => new AppImportBatch(
                [
                    new DictionaryImportCandidate(DictionaryEntryType.Term, "TypeWhisper"),
                    new DictionaryImportCandidate(DictionaryEntryType.Correction, "type whisper", "TypeWhisper"),
                ],
                Excluded: 2
            ),
        };
        using var import = Import(source);

        await import.LoadAsync();

        Assert.Equal("Import from Wispr Flow", import.Title);
        Assert.Equal("2 new · 0 already present · 0 conflicts kept unchanged · 2 excluded", import.Summary);
        Assert.Equal(["TypeWhisper", "type whisper → TypeWhisper"], import.Lines.Select(line => line.Entry));

        Assert.Equal("Dictionary entries added from Wispr Flow: 2.", import.Commit());
        var correction = Assert.Single(_dictionary.Entries, entry => entry.EntryType == DictionaryEntryType.Correction);
        Assert.Equal(("type whisper", "TypeWhisper", DictionaryEntrySource.Import), (correction.Original, correction.Replacement, correction.Source));
    }

    [Fact]
    public async Task AnEditDuringTheReviewIsNotOverwritten()
    {
        using var import = Import(HandyWords("Avalonia"));
        await import.LoadAsync();

        _dictionary.AddEntry(new DictionaryEntry { Id = "meanwhile", EntryType = DictionaryEntryType.Term, Original = "Edited meanwhile" });

        Assert.StartsWith("Your dictionary changed while you were reviewing.", import.Commit());
        Assert.Equal(["Edited meanwhile"], _dictionary.Entries.Select(entry => entry.Original));
    }

    [Fact]
    public async Task AnEmptySourceOffersNothingToAdd()
    {
        using var import = Import(HandyWords());

        await import.LoadAsync();

        Assert.Equal("Handy has no dictionary entries to import.", import.Message);
        Assert.False(import.CanAdd);
    }

    [Fact]
    public async Task AMissingFileExplainsWhereItLooked()
    {
        var path = Path.Join(_directory, "missing", "flow.sqlite");
        using var import = new AppImportViewModel(_dictionary, AppImportSource.WisprFlow, path);

        await import.LoadAsync();

        Assert.Equal($"The file was not found: {path}.", import.Message);
        Assert.False(import.CanAdd);
    }

    [Fact]
    public async Task AFileThatIsNotAWisprDatabaseIsReportedWithoutReviewing()
    {
        var path = Path.Join(_directory, "flow.sqlite");
        await File.WriteAllTextAsync(path, "not a database");
        using var import = new AppImportViewModel(_dictionary, AppImportSource.WisprFlow, path);

        await import.LoadAsync();

        Assert.Equal("This file is not a Wispr Flow database. Nothing was imported.", import.Message);
        Assert.Empty(import.Lines);
    }

    [Theory]
    [InlineData(AppImportReadFailure.Unstable, "Could not read a stable copy of the Wispr Flow data. Quitting Wispr Flow and trying again can help.")]
    [InlineData(AppImportReadFailure.TooManyEntries, "Wispr Flow has more than 10,000 entries. Nothing was imported.")]
    [InlineData(AppImportReadFailure.TooLarge, "This Wispr Flow database is too large to import: over 2 GB, an entry over 10,000 characters, or more than five million characters in total.")]
    [InlineData(AppImportReadFailure.Malformed, "This file is not a Wispr Flow database. Nothing was imported.")]
    [InlineData(AppImportReadFailure.SqliteUnavailable, "Reading this database needs the SQLite library (libsqlite3.so.0), which is not installed. Install your distribution's SQLite package and try again.")]
    public async Task ReadFailuresAreExplainedWithoutReviewing(AppImportReadFailure failure, string message)
    {
        using var import = Import(Failing(failure));

        await import.LoadAsync();

        Assert.Equal(message, import.Message);
        Assert.False(import.CanAdd);
    }

    [Fact]
    public async Task HandyUsesItsOwnMessagesForItsOwnLimits()
    {
        using var import = Import(AppImportSource.Handy((_, _) => "[1]"u8.ToArray()));

        await import.LoadAsync();

        Assert.Equal("This file is not a Handy settings file. Nothing was imported.", import.Message);
    }

    [Fact]
    public async Task AnUnreadableDictionaryIsNotTreatedAsEmpty()
    {
        var dictionary = new Mock<IDictionaryService>();
        IReadOnlyList<DictionaryEntry> none = [];
        dictionary.Setup(d => d.TryGetEntries(out none)).Returns(false);
        using var import = Import(HandyWords("Avalonia"), dictionary.Object);

        await import.LoadAsync();

        Assert.Equal("Your dictionary could not be read, so nothing was imported.", import.Message);
        Assert.False(import.CanAdd);
        dictionary.Verify(d => d.CommitReviewedImport(It.IsAny<DictionaryImportReview>()), Times.Never);
    }

    [Fact]
    public async Task CancelingWhileReadingWritesNothing()
    {
        AppImportViewModel? import = null;
        var source = AppImportSource.WisprFlow with
        {
            Load = (_, token) =>
            {
                // ReSharper disable once AccessToModifiedClosure -- assigned below before LoadAsync runs this source; the point is to cancel from inside the read.
                import!.Cancel();
                token.ThrowIfCancellationRequested();
                return new AppImportBatch([new DictionaryImportCandidate(DictionaryEntryType.Term, "Avalonia")]);
            },
        };
        using var owned = import = Import(source);

        await import.LoadAsync();

        Assert.False(import.HasMessage);
        Assert.Empty(import.Lines);
        Assert.Equal("No new dictionary entries to import.", import.Commit());
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task ClosingTheDialogDuringAHandyRetryEndsTheLoadQuietly()
    {
        using var reading = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        var reads = 0;
        // Reads that never match send the reader into its retry wait on the token after the dialog closed.
        var source = AppImportSource.Handy((_, _) =>
        {
            // ReSharper disable once InvertIf -- both paths end in the same differing read; inverting would duplicate it.
            if (Interlocked.Increment(ref reads) == 1)
            {
                // ReSharper disable AccessToDisposedClosure -- LoadAsync is awaited below, before the usings dispose these events.
                reading.Set();
                closed.Wait(CancellationToken.None);
                // ReSharper restore AccessToDisposedClosure
            }

            return Encoding.UTF8.GetBytes($"{{\"settings\":{{\"custom_words\":[\"{reads}\"]}}}}");
        });
        using var import = Import(source);

        var load = import.LoadAsync();
        reading.Wait();
        import.Cancel();
        // ReSharper disable once DisposeOnUsingVariable -- disposing mid-load is the scenario; the using covers an early throw.
        import.Dispose();
        closed.Set();
        await load;

        Assert.False(import.HasMessage);
        Assert.Empty(import.Lines);
    }

    [Fact]
    public async Task ASaveFailureIsReportedAndLeavesTheDictionaryUnchanged()
    {
        var dictionary = new Mock<IDictionaryService>();
        IReadOnlyList<DictionaryEntry> none = [];
        dictionary.Setup(d => d.TryGetEntries(out none)).Returns(true);
        dictionary.Setup(d => d.CommitReviewedImport(It.IsAny<DictionaryImportReview>()))
            .Throws(new IOException("disk full"));
        using var import = Import(HandyWords("Avalonia"), dictionary.Object);
        await import.LoadAsync();

        Assert.Equal("Could not save the dictionary: disk full. Nothing was imported.", import.Commit());
    }
}
