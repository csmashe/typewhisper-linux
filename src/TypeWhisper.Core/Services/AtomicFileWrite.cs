using System.Runtime.InteropServices;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Writes content so the destination ends up with either the complete old or complete new
///     content, never a partial write. Failures throw.
/// </summary>
public static class AtomicFileWrite
{
    private const int EEXIST = 17;

    // DllImport rather than LibraryImport: the latter's generated marshalling needs
    // AllowUnsafeBlocks, which is not worth enabling project-wide for one call. CharSet.Ansi
    // marshals as UTF-8 on Unix, which is what libc expects for paths.
    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Link(string oldPath, string newPath);

    /// <summary>
    ///     Publishes a fully-written temporary file to <paramref name="path" /> without ever
    ///     replacing an existing destination, so the destination goes straight from absent to
    ///     complete.
    ///     <para>
    ///         <see cref="File.Move(string, string)" /> cannot do this on Unix: its no-overwrite
    ///         guarantee is a check followed by <c>rename(2)</c>, which silently clobbers, so
    ///         concurrent callers all "succeed" and each destroys the previous one's content.
    ///         <c>link(2)</c> fails with <c>EEXIST</c> instead, atomically, which is exactly the
    ///         documented contract. Reserving the destination up front is not an option either —
    ///         that publishes an empty file for the duration of the write.
    ///     </para>
    /// </summary>
    private static void PublishCreateNew(string tempPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // MoveFileEx without MOVEFILE_REPLACE_EXISTING already fails when the destination
            // exists, so the framework call is atomic here.
            File.Move(tempPath, path);
            return;
        }

        if (Link(tempPath, path) == 0)
        {
            // Committed: the destination now names this content and was never visible in a
            // partial state. Dropping the temporary name is cleanup only — throwing here would
            // report failure for a write that succeeded, and callers that retry on IOException
            // (RecorderFileNamer) would then publish a duplicate under the next free name.
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

            if (replaceExisting && File.Exists(path))
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
}
