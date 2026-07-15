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
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
            {
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
