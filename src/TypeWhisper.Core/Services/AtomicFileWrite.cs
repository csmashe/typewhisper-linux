using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TypeWhisper.Core.Services;

public sealed class AtomicFileWriteIndeterminateCommitException : IOException
{
    internal AtomicFileWriteIndeterminateCommitException(
        string path,
        string directoryPath,
        Exception innerException
    )
        : base(
            $"Indeterminate commit for '{path}': publication completed, but syncing parent "
                + $"directory '{directoryPath}' failed. The destination is visible, but its "
                + "crash durability is unknown.",
            innerException
        )
    {
        PublishedPath = path;
    }

    /// <summary>
    ///     The destination that IS visible despite the failed directory sync, so callers
    ///     can record where the content landed instead of reporting a lost write.
    /// </summary>
    public string PublishedPath { get; }
}

/// <summary>
///     Writes content so the destination ends up with either the complete old or complete new
///     content, never a partial write. Failures throw.
/// </summary>
public static partial class AtomicFileWrite
{
    private const int AtCurrentWorkingDirectory = -100;
    private const int OpenReadOnly = 0;

    // Linux asm-generic/fcntl.h defines O_DIRECTORY as octal 00200000 (0x10000).
    // The value applies to both x86-64 and arm64; packaging currently ships linux-x64 only.
    private const int OpenDirectory = 0x10000;

    // ReSharper disable once InconsistentNaming -- POSIX errno macro name; PascalCase would obscure it.
    private const int EEXIST = 17;
    private const uint RenameNoReplace = 1;

    // UTF-8 marshalling is what libc expects for paths.
    [LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags
    );

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);

    private static readonly SyncHooks s_productionSyncHooks = new(
        FlushToDisk,
        FlushDirectoryToDisk
    );

    internal sealed class SyncHooks(
        Action<string, FileStream> syncFile,
        Action<string> syncDirectory
    )
    {
        internal Action<string, FileStream> SyncFile { get; } = syncFile;
        internal Action<string> SyncDirectory { get; } = syncDirectory;
    }

    /// <summary>
    ///     Publishes a fully-written temporary file to <paramref name="path" />, which goes
    ///     straight from absent to complete.
    ///     <para>
    ///         <see cref="File.Move(string, string)" /> cannot do this on Unix: its no-overwrite
    ///         guarantee is a check followed by <c>rename(2)</c>, which silently clobbers, so
    ///         concurrent callers all "succeed" and each destroys the previous one's content.
    ///         <c>link(2)</c> fails with <c>EEXIST</c> atomically instead. Reserving the
    ///         destination up front is not an option either — that publishes an empty file for
    ///         the duration of the write.
    ///     </para>
    /// </summary>
    private static void PublishCreateNew(
        string tempPath,
        string path,
        string directoryPath,
        bool attemptHardLink,
        SyncHooks syncHooks
    )
    {
        SyncTemporaryFile(tempPath, syncHooks);

        if (OperatingSystem.IsWindows())
        {
            // MoveFileEx without MOVEFILE_REPLACE_EXISTING already fails atomically here.
            File.Move(tempPath, path, overwrite: false);
            SyncPublishedDirectory(path, directoryPath, syncHooks);
            return;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- ordered, side-effecting
        // steps, not alternatives on one value: the guard below depends on the errno this Link
        // call sets, so order matters.
        if (attemptHardLink && Link(tempPath, path) == 0)
        {
            // Already committed, so dropping the temporary name is cleanup only: throwing here
            // would report failure for a write that succeeded, and callers that retry on
            // IOException (RecorderFileNamer) would publish a duplicate.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort and deliberately unfiltered: an extra hard link is harmless.
            }

            SyncPublishedDirectory(path, directoryPath, syncHooks);
            return;
        }

        if (attemptHardLink && Marshal.GetLastPInvokeError() == EEXIST)
        {
            throw new IOException($"The file '{path}' already exists.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new IOException(
                $"No atomic no-replace publication primitive is available for '{path}'."
            );
        }

        try
        {
            if (
                RenameAt2(
                    AtCurrentWorkingDirectory,
                    tempPath,
                    AtCurrentWorkingDirectory,
                    path,
                    RenameNoReplace
                ) == 0
            )
            {
                SyncPublishedDirectory(path, directoryPath, syncHooks);
                return;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new IOException(
                $"Could not atomically create '{path}' because renameat2 is unavailable.",
                ex
            );
        }

        var renameError = Marshal.GetLastPInvokeError();
        if (renameError == EEXIST)
        {
            throw new IOException($"The file '{path}' already exists.");
        }

        // RENAME_NOREPLACE is the only safe fallback here. In particular, do not use the
        // framework's Unix no-overwrite move, whose destination check and rename are separate.
        throw new IOException(
            $"Could not atomically create '{path}' without replacing an existing file "
            + $"(renameat2 errno {renameError})."
        );
    }

    /// <summary>
    ///     Publishes a temporary file over <paramref name="path" />, existing or not.
    /// </summary>
    private static void PublishReplace(
        string tempPath,
        string path,
        string directoryPath,
        SyncHooks syncHooks
    )
    {
        if (!File.Exists(path))
        {
            try
            {
                PublishCreateNew(
                    tempPath,
                    path,
                    directoryPath,
                    attemptHardLink: true,
                    syncHooks
                );
                return;
            }
            catch (IOException ex)
                when (ex is not AtomicFileWriteIndeterminateCommitException && File.Exists(path))
            {
                // A concurrent writer created the destination between the check and the link.
                // Replacement is unconditional here, so fall through rather than surfacing the
                // create-new path's "already exists" failure.
            }
        }

        SyncTemporaryFile(
            tempPath,
            syncHooks,
            () =>
            {
                // File.Replace brings the temp file's inode (and mode) into the destination, so
                // copy the destination's mode over first to preserve its permissions. The write
                // handle is already open so a read-only destination mode cannot prevent fsync.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
                }
            }
        );
        File.Replace(tempPath, path, null);
        SyncPublishedDirectory(path, directoryPath, syncHooks);
    }

    public static void WriteAllText(string path, string contents)
    {
        WriteAllText(path, contents, stagedWriteObserver: null);
    }

    /// <summary>
    ///     Test-only overload whose observer runs once the unique sibling is complete, but
    ///     before it is published and before the destination's mode is copied onto it.
    /// </summary>
    internal static void WriteAllText(
        string path,
        string contents,
        Action<string>? stagedWriteObserver,
        SyncHooks? syncHooks = null
    )
    {
        WriteCore(
            path,
            replaceExisting: true,
            tempPath => File.WriteAllText(tempPath, contents),
            stagedWriteObserver,
            syncHooks: syncHooks
        );
    }

    /// <summary>
    ///     Atomically creates <paramref name="path" /> with complete text content. Throws an
    ///     <see cref="IOException" /> without changing the destination when it already exists.
    /// </summary>
    public static void WriteAllTextCreateNew(string path, string contents)
    {
        WriteCore(path, replaceExisting: false, tempPath => File.WriteAllText(tempPath, contents));
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        WriteCore(path, replaceExisting: true, tempPath => File.WriteAllBytes(tempPath, bytes));
    }

    /// <summary>
    ///     Atomically creates <paramref name="path" /> with complete byte content. Throws an
    ///     <see cref="IOException" /> without changing the destination when it already exists.
    /// </summary>
    public static void WriteAllBytesCreateNew(string path, byte[] bytes)
    {
        WriteCore(path, replaceExisting: false, tempPath => File.WriteAllBytes(tempPath, bytes));
    }

    /// <summary>
    ///     Test-only overload for exercising the no-replace fallback without requiring a
    ///     filesystem that rejects hard links. The observer runs in the staged-but-unpublished
    ///     race window the fallback has to survive.
    /// </summary>
    internal static void WriteAllBytesCreateNew(
        string path,
        byte[] bytes,
        bool attemptHardLink,
        Action<string>? stagedWriteObserver = null,
        SyncHooks? syncHooks = null
    )
    {
        WriteCore(
            path,
            replaceExisting: false,
            tempPath => File.WriteAllBytes(tempPath, bytes),
            stagedWriteObserver,
            attemptHardLink,
            syncHooks
        );
    }

    /// <summary>
    ///     Atomically creates <paramref name="path" /> with complete byte content and the
    ///     requested Unix mode already set when the destination becomes visible.
    /// </summary>
    public static void WriteAllBytesCreateNew(
        string path,
        byte[] bytes,
        UnixFileMode unixCreateMode
    )
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "An explicit Unix create mode is not supported on Windows."
            );
        }

        WriteAllBytesCreateNewUnix(path, bytes, unixCreateMode);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteAllBytesCreateNewUnix(
        string path,
        byte[] bytes,
        UnixFileMode unixCreateMode
    )
    {
        WriteCore(
            path,
            replaceExisting: false,
            tempPath =>
            {
                using var stream = new FileStream(
                    tempPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        UnixCreateMode = unixCreateMode,
                    }
                );
                File.SetUnixFileMode(tempPath, unixCreateMode);
                if (File.GetUnixFileMode(tempPath) != unixCreateMode)
                {
                    throw new IOException(
                        $"Could not apply Unix mode '{unixCreateMode}' to '{tempPath}'."
                    );
                }

                stream.Write(bytes);
            }
        );
    }

    private static void WriteCore(
        string path,
        bool replaceExisting,
        Action<string> writeTemporaryFile,
        Action<string>? stagedWriteObserver = null,
        bool attemptHardLink = true,
        SyncHooks? syncHooks = null
    )
    {
        // Preserve the raw parent prefix used by the temp-file and publication syscalls. Lexical
        // normalization would resolve ".." before the kernel follows a preceding symlink, which
        // can select an unrelated directory for fsync (for example, /a/link/.. when link -> /b/c).
        var directoryPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directoryPath))
        {
            directoryPath = ".";
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        syncHooks ??= s_productionSyncHooks;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                writeTemporaryFile(tempPath);
            }
            else if (replaceExisting)
            {
                // Replace path: make the temp owner-only *before* writing, so secret contents
                // are never briefly world-readable to other local users.
                using (
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.None
                    )
                )
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                writeTemporaryFile(tempPath);
            }
            else
            {
                // New file: let the umask govern the mode so exports in shared output folders
                // stay readable to their consumers.
                writeTemporaryFile(tempPath);
            }

            stagedWriteObserver?.Invoke(tempPath);
            if (replaceExisting)
            {
                PublishReplace(tempPath, path, directoryPath, syncHooks);
            }
            else
            {
                PublishCreateNew(tempPath, path, directoryPath, attemptHardLink, syncHooks);
            }
        }
        catch
        {
            if (!File.Exists(tempPath))
            {
                throw;
            }

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp-file cleanup.
            }

            throw;
        }
    }

    /// <summary>
    ///     Forces the finished temporary file out of the page cache before it becomes the
    ///     destination, so a crash cannot leave a renamed-but-empty file behind.
    /// </summary>
    private static void FlushToDisk(string _, FileStream stream)
    {
        stream.Flush(flushToDisk: true);
    }

    private static void SyncTemporaryFile(
        string tempPath,
        SyncHooks syncHooks,
        Action? afterOpen = null
    )
    {
        using var stream = OpenTemporaryFileForSync(tempPath);
        if (stream is null)
        {
            return;
        }

        afterOpen?.Invoke();
        syncHooks.SyncFile(tempPath, stream);
    }

    private static FileStream? OpenTemporaryFileForSync(string tempPath)
    {
        try
        {
            return new FileStream(
                tempPath,
                FileMode.Open,
                // FileAccess.Write is load-bearing: FileStream.Flush(flushToDisk: true)
                // silently does nothing when the stream is not writable.
                FileAccess.Write,
                FileShare.Read
            );
        }
        catch (FileNotFoundException)
        {
            // A vanished temporary has nothing left to make durable; the publication
            // attempt right after this fails closed with its established error surface,
            // which callers already handle.
            return null;
        }
    }

    private static void SyncPublishedDirectory(
        string path,
        string directoryPath,
        SyncHooks syncHooks
    )
    {
        try
        {
            syncHooks.SyncDirectory(directoryPath);
        }
        catch (Exception ex) when (ex is not AtomicFileWriteIndeterminateCommitException)
        {
            throw new AtomicFileWriteIndeterminateCommitException(path, directoryPath, ex);
        }
    }

    /// <summary>
    ///     Opens and syncs a Linux directory so preceding publication metadata reaches stable
    ///     storage. Other platforms retain their existing publication guarantees.
    /// </summary>
    internal static void FlushDirectoryToDisk(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directoryFileDescriptor = Open(directoryPath, OpenReadOnly | OpenDirectory);
        if (directoryFileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Could not open directory '{directoryPath}' for syncing (errno {error}).",
                new Win32Exception(error)
            );
        }

        try
        {
            if (Fsync(directoryFileDescriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Could not sync directory '{directoryPath}' (errno {error}).",
                    new Win32Exception(error)
                );
            }
        }
        finally
        {
            // Linux close(2) may have closed the descriptor even when it reports an error, so
            // retrying could close an unrelated descriptor that another thread just opened.
            _ = Close(directoryFileDescriptor);
        }
    }
}
