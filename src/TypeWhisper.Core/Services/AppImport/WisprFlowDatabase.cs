using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TypeWhisper.Core.Services.AppImport;

/// <summary>
///     Reads Wispr Flow's <c>Dictionary</c> table through the system SQLite library, from a verified copy made by
///     <see cref="StableImportCopy" />; the chosen database is never opened by SQLite.
/// </summary>
internal static partial class WisprFlowDatabase
{
    private const int MaxRows = 10_000;
    private const int MaxTotalCharacters = 5_000_000;
    private const int MaxEntryCharacters = 10_000;
    private const string Library = "libsqlite3.so.0";

    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteTooBig = 18;
    private const int SqliteLimitLength = 0;
    private const int SqliteInteger = 1;
    private const int SqliteText = 3;
    private const int SqliteNull = 5;
    // READWRITE without CREATE lets SQLite rebuild the WAL index, which happens only inside the scratch copy.
    private const int OpenReadWrite = 0x2;

    internal static IReadOnlyList<WisprFlowRow> Read(string path, CancellationToken cancellationToken) =>
        StableImportCopy.Read(path, copy => ReadCopy(copy, cancellationToken), cancellationToken);

    internal static IReadOnlyList<WisprFlowRow> ReadCopy(string copy, CancellationToken cancellationToken)
    {
        try
        {
            return ReadCopyCore(copy, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AppImportReadException(
                AppImportReadFailure.SqliteUnavailable,
                $"The SQLite library ({Library}) is not installed.",
                ex
            );
        }
    }

    private static unsafe List<WisprFlowRow> ReadCopyCore(string copy, CancellationToken cancellationToken)
    {
        var opened = sqlite3_open_v2(copy, out var db, OpenReadWrite, 0);
        var token = default(GCHandle);
        try
        {
            token = GCHandle.Alloc(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (opened != SqliteOk)
            {
                throw Unreadable("SQLite could not open the file.");
            }

            // Lets cancellation interrupt a long scan or sort before any row comes back.
            sqlite3_progress_handler(db, 1000, &OnProgress, GCHandle.ToIntPtr(token));
            // An oversized value then fails with SQLITE_TOOBIG instead of being allocated first.
            _ = sqlite3_limit(db, SqliteLimitLength, MaxEntryCharacters * 4);
            if (sqlite3_exec(db, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;", 0, 0, 0) != SqliteOk)
            {
                throw Unreadable("SQLite could not read the file.");
            }

            // Wispr Flow stores an ordinary table; a view could compute rows of any size or cost.
            if (!HasDictionaryTable(db))
            {
                throw Unreadable("The file has no Wispr Flow Dictionary table.");
            }

            var prepared = sqlite3_prepare_v2(
                db,
                $"SELECT phrase, replacement, isDeleted, isSnippet FROM Dictionary ORDER BY id LIMIT {MaxRows + 1}",
                -1,
                out var statement,
                0
            );
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (prepared != SqliteOk)
                {
                    throw Unreadable("The file has no Wispr Flow Dictionary table.");
                }

                var rows = new List<WisprFlowRow>();
                var characters = 0;
                int result;
                while ((result = sqlite3_step(statement)) == SqliteRow)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Deleted and filtered rows count too, so the limit bounds the work, not just the import.
                    if (rows.Count == MaxRows)
                    {
                        throw new AppImportReadException(
                            AppImportReadFailure.TooManyEntries,
                            $"The database has more than {MaxRows} entries."
                        );
                    }

                    var row = new WisprFlowRow(
                        Text(statement, 0)!,
                        Text(statement, 1, optional: true),
                        Boolean(statement, 2),
                        Boolean(statement, 3)
                    );
                    characters += row.Phrase.Length + (row.Replacement?.Length ?? 0);
                    if (characters > MaxTotalCharacters)
                    {
                        throw TooLarge("The dictionary has more than five million characters of text.");
                    }

                    rows.Add(row);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return result switch
                {
                    SqliteDone => rows,
                    SqliteTooBig => throw TooLarge("An entry exceeds SQLite's length limit for the import."),
                    _ => throw Unreadable("SQLite could not read every row."),
                };
            }
            finally
            {
                if (statement != 0)
                {
                    _ = sqlite3_finalize(statement);
                }
            }
        }
        finally
        {
            if (db != 0)
            {
                sqlite3_progress_handler(db, 0, null, 0);
                _ = sqlite3_close_v2(db);
            }

            if (token.IsAllocated)
            {
                token.Free();
            }
        }
    }

    private static bool HasDictionaryTable(nint db)
    {
        if (sqlite3_prepare_v2(
                db,
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Dictionary' COLLATE NOCASE",
                -1,
                out var statement,
                0
            ) != SqliteOk)
        {
            return false;
        }

        try
        {
            return sqlite3_step(statement) == SqliteRow;
        }
        finally
        {
            _ = sqlite3_finalize(statement);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(nint argument) =>
        GCHandle.FromIntPtr(argument).Target is CancellationToken { IsCancellationRequested: true } ? 1 : 0;

    private static string? Text(nint statement, int column, bool optional = false)
    {
        var type = sqlite3_column_type(statement, column);
        if (optional && type == SqliteNull)
        {
            return null;
        }

        if (type != SqliteText)
        {
            throw Unreadable("A Dictionary column has an unexpected type.");
        }

        var bytes = sqlite3_column_bytes16(statement, column);
        if (bytes > MaxEntryCharacters * 2)
        {
            throw TooLarge("An entry has more than 10,000 characters.");
        }

        return Marshal.PtrToStringUni(sqlite3_column_text16(statement, column), bytes / 2)
            ?? throw Unreadable("A Dictionary value could not be read.");
    }

    private static bool Boolean(nint statement, int column)
    {
        if (sqlite3_column_type(statement, column) != SqliteInteger)
        {
            throw Unreadable("A Dictionary flag is not an integer.");
        }

        return sqlite3_column_int64(statement, column) switch
        {
            0 => false,
            1 => true,
            _ => throw Unreadable("A Dictionary flag is not 0 or 1."),
        };
    }

    private static AppImportReadException Unreadable(string message) => new(AppImportReadFailure.Malformed, message);

    private static AppImportReadException TooLarge(string message) => new(AppImportReadFailure.TooLarge, message);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

    [LibraryImport(Library)]
    private static partial int sqlite3_close_v2(nint db);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_exec(nint db, string sql, nint callback, nint argument, nint error);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_prepare_v2(nint db, string sql, int length, out nint statement, nint tail);

    [LibraryImport(Library)]
    private static partial int sqlite3_limit(nint db, int id, int newValue);

    [LibraryImport(Library)]
    private static partial int sqlite3_step(nint statement);

    [LibraryImport(Library)]
    private static partial int sqlite3_finalize(nint statement);

    [LibraryImport(Library)]
    private static unsafe partial void sqlite3_progress_handler(
        nint db,
        int instructions,
        delegate* unmanaged[Cdecl]<nint, int> callback,
        nint argument
    );

    [LibraryImport(Library)]
    private static partial int sqlite3_column_type(nint statement, int column);

    [LibraryImport(Library)]
    private static partial nint sqlite3_column_text16(nint statement, int column);

    [LibraryImport(Library)]
    private static partial int sqlite3_column_bytes16(nint statement, int column);

    [LibraryImport(Library)]
    private static partial long sqlite3_column_int64(nint statement, int column);
}
