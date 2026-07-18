namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Exact file state used by <see cref="AtomicFileWriter.WriteIfUnchangedAsync" />.
///     The requested path is kept separately from the resolved path so a symlink
///     retarget between capture and commit can be detected.
/// </summary>
internal readonly record struct AtomicFileSnapshot(
    string RequestedTarget,
    string ResolvedTarget,
    bool Existed,
    string Contents
);

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
    public static async Task<AtomicFileSnapshot> CaptureAsync(
        string target,
        CancellationToken ct
    )
    {
        var resolvedTarget = Path.GetFullPath(ResolveWriteTarget(target));
        if (!File.Exists(resolvedTarget))
        {
            return new AtomicFileSnapshot(target, resolvedTarget, false, string.Empty);
        }

        var contents = await File.ReadAllTextAsync(resolvedTarget, ct).ConfigureAwait(false);
        return new AtomicFileSnapshot(target, resolvedTarget, true, contents);
    }

    public static async Task WriteAsync(string target, string contents, CancellationToken ct)
    {
        var resolvedTarget = ResolveWriteTarget(target);
        var tmp = await StageAsync(resolvedTarget, contents, nameof(target), ct)
            .ConfigureAwait(false);
        try
        {
            File.Move(tmp, resolvedTarget, true);
        }
        finally
        {
            DeleteTempBestEffort(tmp);
        }
    }

    /// <summary>
    ///     Atomically replaces the snapshot's resolved file only when the configured
    ///     path still resolves to the same final target and that target's existence and
    ///     exact contents still match the captured state. Returns false on a conflict.
    /// </summary>
    public static async Task<bool> WriteIfUnchangedAsync(
        AtomicFileSnapshot snapshot,
        string contents,
        CancellationToken ct
    )
    {
        var tmp = await StageAsync(
                snapshot.ResolvedTarget,
                contents,
                nameof(snapshot),
                ct
            )
            .ConfigureAwait(false);
        try
        {
            var currentResolved = Path.GetFullPath(ResolveWriteTarget(snapshot.RequestedTarget));
            if (!string.Equals(currentResolved, snapshot.ResolvedTarget, StringComparison.Ordinal))
            {
                return false;
            }

            var currentExists = File.Exists(currentResolved);
            if (currentExists != snapshot.Existed)
            {
                return false;
            }

            if (currentExists)
            {
                string currentContents;
                try
                {
                    currentContents = await File.ReadAllTextAsync(currentResolved, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return false;
                }

                if (!string.Equals(currentContents, snapshot.Contents, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tmp, snapshot.ResolvedTarget, true);
            return true;
        }
        finally
        {
            DeleteTempBestEffort(tmp);
        }
    }

    private static async Task<string> StageAsync(
        string resolvedTarget,
        string contents,
        string argumentName,
        CancellationToken ct
    )
    {
        var dir = Path.GetDirectoryName(resolvedTarget);
        if (string.IsNullOrEmpty(dir))
        {
            throw new ArgumentException(
                "Target path must include a directory.",
                argumentName
            );
        }

        var tmp = Path.Join(
            dir,
            $".{Path.GetFileName(resolvedTarget)}.{Path.GetRandomFileName()}.tmp"
        );
        try
        {
            await File.WriteAllTextAsync(tmp, contents, ct).ConfigureAwait(false);
            // ReSharper disable once InvertIf -- inverting would duplicate the `return tmp` and turn the intent (preserve perms when the target exists) into a harder-to-read early-out.
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

            return tmp;
        }
        catch
        {
            DeleteTempBestEffort(tmp);
            throw;
        }
    }

    private static void DeleteTempBestEffort(string tmp)
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
            // Cleanup of the temp file is best-effort; the original failure wins.
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
