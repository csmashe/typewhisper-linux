using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Writes content so the destination ends up with either the complete old or complete new
///     content, never a partial write. Failures throw.
/// </summary>
public static partial class AtomicFileWrite
{
    // ReSharper disable once InconsistentNaming -- POSIX errno macro name; PascalCase would obscure it.
    private const int EEXIST = 17;

    // UTF-8 marshalling is what libc expects for paths.
    [LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string oldPath, string newPath);

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
    private static void PublishCreateNew(string tempPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // MoveFileEx without MOVEFILE_REPLACE_EXISTING already fails atomically here.
            File.Move(tempPath, path);
            return;
        }

        if (Link(tempPath, path) == 0)
        {
            // Already committed, so dropping the temporary name is cleanup only: throwing here
            // would report failure for a write that succeeded, and callers that retry on
            // IOException (RecorderFileNamer) would publish a duplicate.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: an extra hard link is harmless, the content is already published.
            }

            return;
        }

        if (Marshal.GetLastPInvokeError() == EEXIST)
        {
            throw new IOException($"The file '{path}' already exists.");
        }

        // Filesystems without hard-link support (some FUSE/exFAT mounts) report EPERM/EXDEV/
        // ENOSYS. Fall back to the framework move: weaker under concurrency, but the alternative
        // is failing the write outright on those mounts.
        File.Move(tempPath, path);
    }

    public static void WriteAllText(string path, string contents)
    {
        WriteCore(path, replaceExisting: true, tempPath => File.WriteAllText(tempPath, contents));
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
        Action<string> writeTemporaryFile
    )
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

            FlushToDisk(tempPath);

            if (replaceExisting)
            {
                if (File.Exists(path))
                {
                    // File.Replace brings the temp file's inode (and mode) into the destination, so
                    // copy the destination's mode over first to preserve its permissions.
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
                    }

                    File.Replace(tempPath, path, null);
                }
                else
                {
                    // Overwriting move: a destination created between the check above and the move
                    // must still be replaced rather than fail the write.
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                PublishCreateNew(tempPath, path);
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
    private static void FlushToDisk(string tempPath)
    {
        using var handle = File.OpenHandle(tempPath, FileMode.Open, FileAccess.Write);
        RandomAccess.FlushToDisk(handle);
    }
}
