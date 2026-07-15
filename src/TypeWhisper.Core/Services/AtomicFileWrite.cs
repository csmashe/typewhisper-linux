namespace TypeWhisper.Core.Services;

/// <summary>
///     Writes text so the destination ends up with either the complete old or complete new
///     content, never a partial write. Failures throw.
/// </summary>
public static class AtomicFileWrite
{
    public static void WriteAllText(string path, string contents)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Make the temp file owner-only *before* writing contents, so secrets are never
            // briefly world-readable to other local users.
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(tempPath, contents);
            }
            else
            {
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

                File.WriteAllText(tempPath, contents);
            }

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
                // New file: the owner-only mode set above carries over via the move.
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
