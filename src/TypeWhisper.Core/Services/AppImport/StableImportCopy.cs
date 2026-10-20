using System.Security.Cryptography;

namespace TypeWhisper.Core.Services.AppImport;

/// <summary>
///     Copies a SQLite database and its write-ahead log into a private scratch folder and hands the copy to a
///     reader, only once the source's size, timestamp and SHA-256 hashes match before and after the copy and the
///     copy's hashes match the source's. The source is never opened by SQLite, because even a reader can create
///     or change its <c>-wal</c>/<c>-shm</c> sidecars. Ported from upstream TypeWhisper #550.
/// </summary>
internal static class StableImportCopy
{
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    internal const string ScratchPrefix = "typewhisper-import-";
    private const int Attempts = 3;
    private const int BufferBytes = 80 * 1024;
    private const string CopyName = "source.sqlite";
    private const string LeaseName = ".lease";
    private static readonly string[] s_suffixes = ["", "-wal", "-shm", "-journal"];
    private const int JournalIndex = 3;
    private const int CopiedParts = 2;

    private sealed record Part(bool Exists, long Length = 0, long Written = 0, string? Hash = null);

    /// <param name="source">The database to copy; its <c>-wal</c> is copied beside it when present.</param>
    /// <param name="read">Reads the verified copy, while it still exists.</param>
    /// <param name="cancellationToken">Stops copying, hashing and reading.</param>
    /// <param name="afterCopy">Test seam run between copying and re-inspecting the source.</param>
    /// <param name="scratchParent">Test seam replacing the temp directory.</param>
    internal static T Read<T>(
        string source,
        Func<string, T> read,
        CancellationToken cancellationToken,
        Action? afterCopy = null,
        string? scratchParent = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        scratchParent ??= Path.GetTempPath();
        CleanupAbandonedCopies(scratchParent);
        var directory = Path.Join(scratchParent, ScratchPrefix + Guid.NewGuid().ToString("N"));
        FileStream? lease = null;
        try
        {
            // Private to this user: the copy holds another app's dictionary.
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // FileShare.None takes an advisory lock, which keeps a concurrent cleanup from deleting this copy.
            lease = new FileStream(Path.Join(directory, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            // Only acquiring the copy retries; a stable copy that fails to read fails once.
            var copy = Acquire(source, directory, afterCopy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return read(copy);
        }
        finally
        {
            lease?.Dispose();
            TryDelete(directory);
        }
    }

    private static string Acquire(string source, string directory, Action? afterCopy, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptDirectory = Path.Join(directory, attempt.ToString());
            try
            {
                var before = Inspect(source, cancellationToken);
                var copy = Path.Join(attemptDirectory, CopyName);
                Directory.CreateDirectory(attemptDirectory);
                var remaining = MaxBytes;
                for (var index = 0; index < CopiedParts; index++)
                {
                    if (before[index].Exists)
                    {
                        remaining = CopyBounded(source + s_suffixes[index], copy + s_suffixes[index], remaining, cancellationToken);
                    }
                }

                afterCopy?.Invoke();
                var after = Inspect(source, cancellationToken);
                if (before.SequenceEqual(after)
                    && Enumerable.Range(0, CopiedParts).All(i =>
                        !before[i].Exists || HashFile(copy + s_suffixes[i], cancellationToken) == before[i].Hash))
                {
                    return copy;
                }
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                // The source was being written, or had a transient journal; try again.
            }

            TryDelete(attemptDirectory);
        }

        throw new AppImportReadException(AppImportReadFailure.Unstable, "Could not obtain a stable copy of the database.");
    }

    private static long CopyBounded(string from, string to, long remaining, CancellationToken cancellationToken)
    {
        using var input = OpenShared(from);
        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[BufferBytes];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining -= count;
            if (remaining < 0)
            {
                throw TooLarge();
            }

            output.Write(buffer, 0, count);
        }

        return remaining;
    }

    private static Part[] Inspect(string source, CancellationToken cancellationToken)
    {
        var result = new Part[s_suffixes.Length];
        var remaining = MaxBytes;
        for (var i = 0; i < s_suffixes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(source + s_suffixes[i]);
            if (!info.Exists)
            {
                result[i] = i == 0
                    ? throw new FileNotFoundException("The database was not found.", source)
                    : new Part(false);
                continue;
            }

            // A rollback journal means an interrupted write whose recovery would need the original; a link could
            // be redirected between inspection and copy.
            if (i == JournalIndex || info.LinkTarget is not null)
            {
                throw new IOException("The database cannot be copied safely.");
            }

            remaining -= info.Length;
            if (remaining < 0)
            {
                throw TooLarge();
            }

            result[i] = new Part(
                true,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                i < CopiedParts ? HashFile(info.FullName, cancellationToken) : null
            );
        }

        return result;
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenShared(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        var remaining = MaxBytes;
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining -= count;
            if (remaining < 0)
            {
                throw TooLarge();
            }

            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static AppImportReadException TooLarge() => new(AppImportReadFailure.TooLarge, "The database exceeds 2 GB.");

    /// <summary>
    ///     Removes scratch folders left by a crashed import. The age check protects a folder still being created;
    ///     the lease protects one in use.
    /// </summary>
    internal static void CleanupAbandonedCopies(string parent)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(parent, ScratchPrefix + "*"))
            {
                try
                {
                    var info = new DirectoryInfo(path);
                    if (!Guid.TryParseExact(info.Name[ScratchPrefix.Length..], "N", out _)
                        || info.LinkTarget is not null
                        || info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1))
                    {
                        continue;
                    }

                    using (new FileStream(Path.Join(path, LeaseName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                    }

                    TryDelete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or not ours to remove; a later import retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp directory is unreadable; there is nothing to clean.
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for CleanupAbandonedCopies.
        }
    }
}
