using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

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
    string Contents,
    UnixFileMode? Mode
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
internal static partial class AtomicFileWriter
{
    public static async Task<AtomicFileSnapshot> CaptureAsync(
        string target,
        CancellationToken ct
    )
    {
        var resolvedTarget = Path.GetFullPath(ResolveWriteTarget(target));
        if (!File.Exists(resolvedTarget))
        {
            return new AtomicFileSnapshot(target, resolvedTarget, false, string.Empty, null);
        }

        var contents = await File.ReadAllTextAsync(resolvedTarget, ct).ConfigureAwait(false);
        UnixFileMode? mode = OperatingSystem.IsWindows()
            ? null
            : File.GetUnixFileMode(resolvedTarget);
        return new AtomicFileSnapshot(target, resolvedTarget, true, contents, mode);
    }

    public static async Task WriteAsync(string target, string contents, CancellationToken ct)
    {
        var resolvedTarget = ResolveWriteTarget(target);
        var mode = !OperatingSystem.IsWindows() && File.Exists(resolvedTarget)
            ? File.GetUnixFileMode(resolvedTarget)
            : PrivateConfigMode;
        var tmp = await StageAsync(resolvedTarget, contents, mode, nameof(target), ct)
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
    ///     Replaces the snapshot's resolved file only when the configured path still
    ///     resolves to the same final target and that target's existence, exact contents,
    ///     and mode still match the captured state. Returns false on a conflict.
    ///     The replace itself is atomic, but the compare and the replace are two
    ///     operations: POSIX offers no content-conditional rename, so a writer that lands
    ///     between them is still lost. The window is one syscall wide and callers are
    ///     user-initiated setup actions, so it is accepted rather than papered over with a
    ///     quarantine-and-restore dance that has wider failure modes of its own.
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
                snapshot.Mode ?? PrivateConfigMode,
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

                if (
                    !OperatingSystem.IsWindows()
                    && File.GetUnixFileMode(currentResolved) != snapshot.Mode
                )
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

    /// <summary>
    ///     Deletes a directly requested regular file only when its resolution,
    ///     contents, and mode still match the snapshot. A symlink target is never
    ///     deleted through the link; callers should publish an empty replacement
    ///     when preserving the linked container is required.
    ///     Carries the same one-syscall check-then-act window as
    ///     <see cref="WriteIfUnchangedAsync" />.
    /// </summary>
    public static async Task<bool> DeleteIfUnchangedAsync(
        AtomicFileSnapshot snapshot,
        CancellationToken ct
    )
    {
        if (!snapshot.Existed)
        {
            return false;
        }

        var currentResolved = Path.GetFullPath(ResolveWriteTarget(snapshot.RequestedTarget));
        if (
            !string.Equals(currentResolved, snapshot.ResolvedTarget, StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFullPath(snapshot.RequestedTarget),
                snapshot.ResolvedTarget,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

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

        if (
            !OperatingSystem.IsWindows()
            && File.GetUnixFileMode(currentResolved) != snapshot.Mode
        )
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        File.Delete(snapshot.ResolvedTarget);
        return true;
    }

    private static async Task<string> StageAsync(
        string resolvedTarget,
        string contents,
        UnixFileMode mode,
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
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = mode;
            }

            await using (var stream = new FileStream(tmp, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (OperatingSystem.IsWindows())
            {
                return tmp;
            }

            File.SetUnixFileMode(tmp, mode);
            return File.GetUnixFileMode(tmp) == mode
                ? tmp
                : throw new IOException($"Could not apply mode {mode} to '{tmp}'.");
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
        var requestedKind = GetPathKind(target);
        if (requestedKind is AtomicPathKind.Absent or AtomicPathKind.Regular)
        {
            return target;
        }

        if (requestedKind != AtomicPathKind.Symlink)
        {
            throw new IOException(
                $"'{target}' is not a regular file. Refusing to replace a non-file entry."
            );
        }

        FileSystemInfo? resolved;
        try
        {
            resolved = File.ResolveLinkTarget(target, returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            throw new IOException($"'{target}' changed while its symbolic link was resolved.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new IOException($"'{target}' changed while its symbolic link was resolved.");
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"'{target}' is a symbolic link that could not be resolved (possible cycle): "
                + $"{ex.Message}",
                ex
            );
        }

        if (resolved is null || GetPathKind(resolved.FullName) != AtomicPathKind.Regular)
        {
            throw new IOException(
                $"'{target}' is a symbolic link to '{resolved?.FullName ?? "(unresolved)"}', which does not exist "
                + "or is not a regular file. Refusing to write through a broken link — fix or "
                + "remove it and try again."
            );
        }

        return resolved.FullName;
    }

    private const UnixFileMode PrivateConfigMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static AtomicPathKind GetPathKind(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (info.LinkTarget is not null)
            {
                return AtomicPathKind.Symlink;
            }

            if (info.Exists)
            {
                return AtomicPathKind.Regular;
            }

            return Directory.Exists(path) ? AtomicPathKind.Other : AtomicPathKind.Absent;
        }

        const int atFdcwd = -100;
        const int atSymlinkNoFollow = 0x100;
        const uint statxType = 0x0001;
        var result = statx(atFdcwd, path, atSymlinkNoFollow, statxType, out var stat);
        if (result == 0)
        {
            return (ushort)(stat.Mode & 0xF000) switch
            {
                0x8000 => AtomicPathKind.Regular,
                0xA000 => AtomicPathKind.Symlink,
                _ => AtomicPathKind.Other,
            };
        }

        var error = Marshal.GetLastPInvokeError();
        // Anything past ENOENT/ENOTDIR (EACCES, ELOOP…) is a real failure, and callers filter
        // on IOException — a bare Win32Exception would sail past every one of them.
        return error is 2 or 20
            ? AtomicPathKind.Absent
            : throw new IOException(
                $"Could not inspect '{path}'.",
                new Win32Exception(error)
            );
    }

    private enum AtomicPathKind
    {
        Absent,
        Regular,
        Symlink,
        Other,
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
    }

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out StatxBuffer buffer
    );
}
