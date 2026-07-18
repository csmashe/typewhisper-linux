namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Shared atomic file-write helper for the per-desktop shortcut writers.
///     Writes to a sibling temp file then <see cref="File.Move(string,string,bool)" />s
///     it over the target so the destination never exists half-written.
///     When the target already exists, its Unix permission bits are copied
///     onto the temp file first — a user who hardened their compositor
///     config to e.g. 0600 keeps that mode across our writes.
///     If the target is (or sits behind a chain of) a symbolic link, the
///     write is redirected through the chain to its final regular file so replacing the
///     destination directory entry never unlinks a dotfile-manager-owned symlink
///     (stow/chezmoi/home-manager commonly manage hyprland.conf/sway config this way).
/// </summary>
internal static class AtomicFileWriter
{
    public static async Task WriteAsync(string target, string contents, CancellationToken ct)
    {
        var resolvedTarget = ResolveWriteTarget(target);
        var dir = Path.GetDirectoryName(resolvedTarget);
        if (string.IsNullOrEmpty(dir))
        {
            throw new ArgumentException("Target path must include a directory.", nameof(target));
        }

        var tmp = Path.Join(
            dir,
            $".{Path.GetFileName(resolvedTarget)}.{Path.GetRandomFileName()}.tmp"
        );
        try
        {
            await File.WriteAllTextAsync(tmp, contents, ct).ConfigureAwait(false);
            if (File.Exists(resolvedTarget) && !OperatingSystem.IsWindows())
            {
                // Preserve a user-hardened config's permission bits.
                try
                {
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(resolvedTarget));
                }
                catch
                {
                    /* unsupported FS — best effort */
                }
            }

            File.Move(tmp, resolvedTarget, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Cleanup of the temp file is best-effort; the original failure is rethrown below.
            }

            throw;
        }
    }

    /// <summary>
    ///     Resolves <paramref name="target" /> to the file that should actually receive the
    ///     write: itself, when it is not a symlink (including when nothing exists there yet —
    ///     the common first-run case), or the final regular file at the end of its symlink
    ///     chain (following relative targets and multiple hops), so the atomic replace never
    ///     unlinks a dotfile-manager-owned symlink. Refuses with an actionable message if the
    ///     chain is broken, cyclic, or resolves to something other than a regular file.
    /// </summary>
    private static string ResolveWriteTarget(string target)
    {
        FileSystemInfo? resolved;
        try
        {
            resolved = File.ResolveLinkTarget(target, returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            // Nothing exists at this path yet (first-run install) — write it directly.
            return target;
        }
        catch (DirectoryNotFoundException)
        {
            return target;
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"'{target}' is a symbolic link that could not be resolved (possible cycle): "
                + $"{ex.Message}",
                ex
            );
        }

        if (resolved is null)
        {
            // Not a symlink: a plain file (or nothing there yet).
            return target;
        }

        if (!File.Exists(resolved.FullName))
        {
            throw new IOException(
                $"'{target}' is a symbolic link to '{resolved.FullName}', which does not exist "
                + "or is not a regular file. Refusing to write through a broken link — fix or "
                + "remove it and try again."
            );
        }

        return resolved.FullName;
    }
}
