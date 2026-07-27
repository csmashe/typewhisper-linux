namespace TypeWhisper.Core.Services;

/// <summary>
///     Writes content so the destination ends up with either the complete old or complete new
///     content, never a partial write. Failures throw.
/// </summary>
public static class AtomicFileWrite
{
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
                File.Move(tempPath, path);
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
