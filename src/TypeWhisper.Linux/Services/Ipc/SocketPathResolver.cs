using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
///     Resolves the path to the TypeWhisper IPC control socket and ensures the
///     containing directory exists with user-only permissions.
/// </summary>
/// <remarks>
///     Preferred: <c>$XDG_RUNTIME_DIR/typewhisper/control.sock</c> (runtime dir is
///     already 0700 via systemd-logind). Falls back to <c>/tmp/typewhisper-$UID/</c>
///     with an explicit chmod 0700 when <c>XDG_RUNTIME_DIR</c> is unset.
/// </remarks>
internal static class SocketPathResolver
{
    private const string SocketFileName = "control.sock";

    // statx(2) ABI: kernel-defined struct, arch-independent. stx_uid is at
    // offset 20 (after stx_mask:4, stx_blksize:4, stx_attributes:8, stx_nlink:4).
    // We allocate the full 256-byte buffer the kernel writes into and read
    // only the owner uid.
    private const int StatxBufSize = 256;
    private const int StatxUidOffset = 20;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxUid = 0x00000008;

    /// <summary>
    ///     Resolves the control-socket path, creating any missing parent
    ///     directories with appropriate permissions. Does not create the socket
    ///     file itself — that's the server's job.
    /// </summary>
    public static string ResolveControlSocketPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
        {
            var dir = Path.Join(xdg, "typewhisper");
            try
            {
                Directory.CreateDirectory(dir);
                // Explicit chmod is cheap insurance against odd umasks.
                TryChmod(dir, 0b111_000_000); // 0700
                return Path.Join(dir, SocketFileName);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[SocketPathResolver] XDG path {dir} unusable: {ex.Message}. Falling back to /tmp."
                );
            }
        }

        var uid = (int)geteuid();
        var fallback = $"/tmp/typewhisper-{uid}";

        // /tmp is world-writable, so a hostile local user could pre-create
        // this directory with permissive modes. If it exists with wrong bits,
        // we try chmod; if verification still fails we use a per-process
        // scratch dir rather than binding inside an attacker-controlled path.
        try
        {
            if (!Directory.Exists(fallback))
            {
                Directory.CreateDirectory(fallback);
            }

            TryChmod(fallback, 0b111_000_000); // 0700
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not prepare {fallback}: {ex.Message}");
            return CreatePrivateSocketPath(uid);
        }

        if (!IsDirectoryPrivateAndOwned(fallback, uid))
        {
            return CreatePrivateSocketPath(uid);
        }

        return Path.Combine(fallback, SocketFileName);
    }

    /// <summary>Best-effort <c>chmod</c>; logs on failure but never throws.</summary>
    public static void TryChmod(string path, uint mode)
    {
        try
        {
            var rc = chmod(path, mode);
            if (rc != 0)
            {
                Trace.WriteLine(
                    $"[SocketPathResolver] chmod({path}, 0{Convert.ToString(mode, 8)}) returned {rc}."
                );
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] chmod({path}) threw: {ex.Message}");
        }
    }

    private static string CreatePrivateSocketPath(int uid)
    {
        var privatePath = Path.Combine(
            Path.GetTempPath(),
            $"typewhisper-{uid}-{Environment.ProcessId}"
        );
        Directory.CreateDirectory(privatePath);
        TryChmod(privatePath, 0b111_000_000); // 0700
        // If chmod didn't take (read-only FS, odd mount), refuse rather than
        // expose a group/other-readable socket — the caller surfaces the exception.
        if (!IsDirectoryPrivateAndOwned(privatePath, uid))
        {
            try
            {
                Directory.Delete(privatePath, true);
            }
            catch
            {
                /* best effort */
            }

            throw new IOException(
                $"Could not secure private socket directory {privatePath} with mode 0700."
            );
        }

        Trace.WriteLine($"[SocketPathResolver] Using private socket directory {privatePath}.");
        return Path.Combine(privatePath, SocketFileName);
    }

    private static bool IsDirectoryPrivateAndOwned(string path, int uid)
    {
        try
        {
            if (!TryGetOwnerUid(path, out var ownerUid))
            {
                Trace.WriteLine($"[SocketPathResolver] Could not determine owner of {path}.");
                return false;
            }

            if (ownerUid != uid)
            {
                Trace.WriteLine(
                    $"[SocketPathResolver] {path} not owned by uid {uid} (actual {ownerUid})."
                );
                return false;
            }

            return DirectoryHasExpectedMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not validate {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetOwnerUid(string path, out int ownerUid)
    {
        ownerUid = -1;
        var buffer = Marshal.AllocHGlobal(StatxBufSize);
        try
        {
            for (var i = 0; i < StatxBufSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            var rc = statx(AtFdcwd, path, AtSymlinkNofollow, StatxUid, buffer);
            if (rc != 0)
            {
                Trace.WriteLine($"[SocketPathResolver] statx({path}) returned {rc}.");
                return false;
            }

            ownerUid = Marshal.ReadInt32(buffer, StatxUidOffset);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] statx({path}) threw: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool DirectoryHasExpectedMode(string path, UnixFileMode expected)
    {
        try
        {
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            // We require user-only bits with no group/other access.
            const UnixFileMode forbidden =
                UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            return (mode & forbidden) == 0 && (mode & expected) == expected;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not stat {path}: {ex.Message}");
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        IntPtr statxbuf
    );
}