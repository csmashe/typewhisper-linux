namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// Shared atomic file-write helper for the per-desktop shortcut writers.
/// Writes to a sibling temp file then <see cref="File.Move(string,string,bool)"/>s
/// it over the target so the destination never exists half-written.
/// When the target already exists, its Unix permission bits are copied
/// onto the temp file first — a user who hardened their compositor
/// config to e.g. 0600 keeps that mode across our writes.
/// </summary>
internal static class AtomicFileWriter
{
    public static async Task WriteAsync(string target, string contents, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            await File.WriteAllTextAsync(tmp, contents, ct).ConfigureAwait(false);
            if (File.Exists(target) && !OperatingSystem.IsWindows())
            {
                // Preserve a user-hardened config's permission bits.
                try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(target)); }
                catch { /* unsupported FS — best effort */ }
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
