using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.AppImport;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services.AppImport;

/// <summary>
///     Covers <see cref="WisprFlowImport" />, <see cref="WisprFlowDatabase" /> and <see cref="StableImportCopy" />
///     against real SQLite files with Wispr Flow's <c>Dictionary</c> schema (from upstream TypeWhisper #550's fixtures).
/// </summary>
public sealed partial class WisprFlowImportTests : IDisposable
{
    private static readonly string[] s_sourceSuffixes = ["", "-wal", "-shm", "-journal"];
    private const string Schema = "CREATE TABLE Dictionary(id INTEGER PRIMARY KEY,phrase TEXT,replacement TEXT,isDeleted,isSnippet);";
    private readonly string _directory = TestPaths.CreateTempDirectory("wispr-import");
    private readonly string _scratch;

    public WisprFlowImportTests()
    {
        _scratch = Path.Join(_directory, "scratch");
        Directory.CreateDirectory(_scratch);
    }

    private string Source => Path.Join(_directory, WisprFlowImport.DatabaseFileName);

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void MapsCanonicalSpellingsToTermsAndCorrectionsAndExcludesDeletedRowsAndSnippets()
    {
        var batch = WisprFlowImport.Map(
            [
                new WisprFlowRow("type whisper", "TypeWhisper", Deleted: false, Snippet: false),
                new WisprFlowRow(" übermorgen ", null, Deleted: false, Snippet: false),
                new WisprFlowRow("same", " same ", Deleted: false, Snippet: false),
                new WisprFlowRow("deleted", null, Deleted: true, Snippet: false),
                new WisprFlowRow("signature", "Hello", Deleted: false, Snippet: true),
            ],
            CancellationToken.None
        );

        Assert.Equal(
            [
                new DictionaryImportCandidate(DictionaryEntryType.Term, "TypeWhisper"),
                new DictionaryImportCandidate(DictionaryEntryType.Correction, "type whisper", "TypeWhisper"),
                new DictionaryImportCandidate(DictionaryEntryType.Term, "übermorgen"),
                new DictionaryImportCandidate(DictionaryEntryType.Term, "same"),
            ],
            batch.Candidates
        );
        Assert.Equal(2, batch.Excluded);
    }

    [SqliteFact]
    public void ReadsRowsFromAWalDatabaseWithoutChangingAnySourceFile()
    {
        using (var db = Sqlite.Create(Source, wal: true))
        {
            db.Execute(Schema);
            db.Execute(
                "INSERT INTO Dictionary VALUES(1,'übermorgen',NULL,0,0);"
                + "INSERT INTO Dictionary VALUES(2,'signature','line 1'||char(10)||'line 2',0,1);"
            );
            // The rows are still only in the -wal file while this connection is open.
            Assert.True(File.Exists(Source + "-wal"));
            var before = SourceHashes();

            var rows = StableImportCopy.Read(
                Source,
                copy => WisprFlowDatabase.ReadCopy(copy, CancellationToken.None),
                CancellationToken.None,
                scratchParent: _scratch
            );

            Assert.Equal(
                [
                    new WisprFlowRow("übermorgen", null, Deleted: false, Snippet: false),
                    new WisprFlowRow("signature", "line 1\nline 2", Deleted: false, Snippet: true),
                ],
                rows
            );
            Assert.Equal(before, SourceHashes());
        }

        Assert.Empty(Directory.GetDirectories(_scratch));
    }

    [SqliteFact]
    public void AWrongFlagTypeFailsWithoutAPartialRead()
    {
        using (var db = Sqlite.Create(Source))
        {
            db.Execute(Schema);
            db.Execute("INSERT INTO Dictionary VALUES(1,'valid',NULL,0,0); INSERT INTO Dictionary VALUES(2,'invalid',NULL,'false',0);");
        }

        Assert.Equal(AppImportReadFailure.Malformed, ReadFailure());
    }

    [SqliteFact]
    public void AFileWithoutTheDictionaryTableIsMalformed()
    {
        using (var db = Sqlite.Create(Source))
        {
            db.Execute("CREATE TABLE Other(id INTEGER PRIMARY KEY);");
        }

        Assert.Equal(AppImportReadFailure.Malformed, ReadFailure());
    }

    [SqliteFact]
    public void ANonDatabaseFileIsMalformed()
    {
        File.WriteAllText(Source, "{\"not\": \"a database\"}");

        Assert.Equal(AppImportReadFailure.Malformed, ReadFailure());
    }

    [SqliteFact]
    public void TheRowLimitCountsDeletedRowsBeforeFiltering()
    {
        using (var db = Sqlite.Create(Source))
        {
            db.Execute(Schema);
            db.Execute(
                "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) "
                + "INSERT INTO Dictionary SELECT x,'deleted',NULL,1,0 FROM n;"
            );
        }

        Assert.Equal(AppImportReadFailure.TooManyEntries, ReadFailure());
    }

    [SqliteFact]
    public void AnEntryOverTheLengthLimitIsTooLarge()
    {
        using (var db = Sqlite.Create(Source))
        {
            db.Execute(Schema);
            db.Execute("INSERT INTO Dictionary VALUES(1,replace(hex(zeroblob(10001)),'00','x'),NULL,0,0);");
        }

        Assert.Equal(AppImportReadFailure.TooLarge, ReadFailure());
    }

    [SqliteFact]
    public void AViewNamedDictionaryIsRejectedBeforeItRuns()
    {
        using (var db = Sqlite.Create(Source))
        {
            // A 4 KB file whose view would build a 100-million-character value on every row.
            db.Execute(
                "CREATE VIEW Dictionary AS SELECT 1 AS id,printf('%.*c',100000000,'x') AS phrase,"
                + "NULL AS replacement,0 AS isDeleted,0 AS isSnippet;"
            );
        }

        Assert.Equal(AppImportReadFailure.Malformed, ReadFailure());
    }

    [SqliteFact]
    public void AValueOverSqlitesLengthCapIsTooLarge()
    {
        using (var db = Sqlite.Create(Source))
        {
            db.Execute(Schema);
            db.Execute("INSERT INTO Dictionary VALUES(1,replace(hex(zeroblob(50000)),'00','x'),NULL,0,0);");
        }

        Assert.Equal(AppImportReadFailure.TooLarge, ReadFailure());
    }

    [Fact]
    public void AMissingDatabaseIsNotFoundAndLeavesNoScratchFolder()
    {
        Assert.Throws<FileNotFoundException>(() =>
            StableImportCopy.Read(Source, _ => 0, CancellationToken.None, scratchParent: _scratch)
        );
        Assert.Empty(Directory.GetDirectories(_scratch));
    }

    [Fact]
    public void ARollbackJournalPreventsTheCopy()
    {
        File.WriteAllText(Source, "source");
        File.WriteAllText(Source + "-journal", "pending");
        var read = false;

        var ex = Assert.Throws<AppImportReadException>(() =>
            StableImportCopy.Read(Source, _ => read = true, CancellationToken.None, scratchParent: _scratch)
        );

        Assert.Equal(AppImportReadFailure.Unstable, ex.Failure);
        Assert.False(read);
        Assert.Empty(Directory.GetDirectories(_scratch));
    }

    [Fact]
    public void ASymlinkedSourceIsNotCopied()
    {
        var target = Path.Join(_directory, "elsewhere.sqlite");
        File.WriteAllText(target, "source");
        File.CreateSymbolicLink(Source, target);

        var ex = Assert.Throws<AppImportReadException>(() =>
            StableImportCopy.Read(Source, _ => 0, CancellationToken.None, scratchParent: _scratch)
        );

        Assert.Equal(AppImportReadFailure.Unstable, ex.Failure);
    }

    [Fact]
    public void ASourceThatChangesDuringEveryCopyIsRejectedAsUnstable()
    {
        File.WriteAllText(Source, "source");
        var copies = 0;
        var read = false;

        var ex = Assert.Throws<AppImportReadException>(() =>
            StableImportCopy.Read(
                Source,
                _ => read = true,
                CancellationToken.None,
                // Same length, so only the hash or timestamp can tell the copy is stale.
                afterCopy: () => File.WriteAllText(Source, $"sourc{copies++ % 10}"),
                scratchParent: _scratch
            )
        );

        Assert.Equal(AppImportReadFailure.Unstable, ex.Failure);
        Assert.Equal(3, copies);
        Assert.False(read);
        Assert.Empty(Directory.GetDirectories(_scratch));
    }

    [Fact]
    public void TheReaderSeesAVerifiedCopyNotTheSource()
    {
        File.WriteAllText(Source, "source");
        File.WriteAllText(Source + "-wal", "wal");

        var (copy, content, wal) = StableImportCopy.Read(
            Source,
            path => (path, File.ReadAllText(path), File.ReadAllText(path + "-wal")),
            CancellationToken.None,
            scratchParent: _scratch
        );

        Assert.StartsWith(Path.Join(_scratch, StableImportCopy.ScratchPrefix), copy);
        Assert.Equal(("source", "wal"), (content, wal));
        Assert.Empty(Directory.GetDirectories(_scratch));
    }

    [Fact]
    public void CleanupRemovesOnlyOldUnleasedScratchFolders()
    {
        var old = Path.Join(_scratch, StableImportCopy.ScratchPrefix + Guid.NewGuid().ToString("N"));
        var recent = Path.Join(_scratch, StableImportCopy.ScratchPrefix + Guid.NewGuid().ToString("N"));
        var leased = Path.Join(_scratch, StableImportCopy.ScratchPrefix + Guid.NewGuid().ToString("N"));
        var foreign = Path.Join(_scratch, StableImportCopy.ScratchPrefix + "not-a-guid");
        foreach (var path in new[] { old, recent, leased, foreign })
        {
            Directory.CreateDirectory(path);
        }

        using var lease = new FileStream(Path.Join(leased, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        foreach (var path in new[] { old, leased, foreign })
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }

        StableImportCopy.CleanupAbandonedCopies(_scratch);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(leased));
        Assert.True(Directory.Exists(foreign));
    }

    private AppImportReadFailure ReadFailure() =>
        Assert.Throws<AppImportReadException>(() =>
            StableImportCopy.Read(
                Source,
                copy => WisprFlowDatabase.ReadCopy(copy, CancellationToken.None),
                CancellationToken.None,
                scratchParent: _scratch
            )
        ).Failure;

    private string[] SourceHashes() =>
        s_sourceSuffixes
            .Select(suffix =>
            {
                if (!File.Exists(Source + suffix))
                {
                    return "missing";
                }

                using var stream = new FileStream(Source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            })
            .ToArray();

    /// <summary>Builds fixture databases through the same system library the importer uses.</summary>
    private sealed partial class Sqlite : IDisposable
    {
        private const string Library = "libsqlite3.so.0";
        private readonly nint _db;

        private Sqlite(nint db)
        {
            _db = db;
        }

        public static Sqlite Create(string path, bool wal = false)
        {
            // READWRITE | CREATE
            Assert.Equal(0, sqlite3_open_v2(path, out var db, 0x2 | 0x4, 0));
            var sqlite = new Sqlite(db);
            if (wal)
            {
                sqlite.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            }

            return sqlite;
        }

        public void Execute(string sql)
        {
            Assert.Equal(0, sqlite3_exec(_db, sql, 0, 0, 0));
        }

        public void Dispose()
        {
            _ = sqlite3_close_v2(_db);
        }

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_exec(nint db, string sql, nint callback, nint argument, nint error);

        [LibraryImport(Library)]
        private static partial int sqlite3_close_v2(nint db);
    }
}

/// <summary>Skips when the system SQLite library the Wispr Flow import relies on is not installed.</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!NativeLibrary.TryLoad("libsqlite3.so.0", out var handle))
        {
            Skip = "Requires the system SQLite library (libsqlite3.so.0).";
            return;
        }

        NativeLibrary.Free(handle);
    }
}
