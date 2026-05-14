using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
/// Resolves the path to the TypeWhisper IPC control socket and ensures the
/// containing directory exists with user-only permissions.
/// </summary>
/// <remarks>
/// Preferred location is <c>$XDG_RUNTIME_DIR/typewhisper/control.sock</c>. The
/// runtime dir is already owned by the user and created with mode 0700 by
/// systemd-logind, so we only need to create the <c>typewhisper/</c> subdir.
/// If <c>XDG_RUNTIME_DIR</c> is unset (older logind, sandbox, custom setups)
/// we fall back to <c>/tmp/typewhisper-$UID/</c> and explicitly chmod 0700 so
/// other local users can't probe or hijack the socket.
/// </remarks>
internal static class SocketPathResolver
{
    private const string SocketFileName = "control.sock";

    /// <summary>
    /// Resolves the control-socket path, creating any missing parent
    /// directories with appropriate permissions. Does not create the socket
    /// file itself — that's the server's job.
    /// </summary>
    public static string ResolveControlSocketPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
        {
            var dir = Path.Combine(xdg, "typewhisper");
            try
            {
                Directory.CreateDirectory(dir);
                // XDG_RUNTIME_DIR is 0700 by systemd-logind; inherited
                // permissions are fine. Explicit chmod is cheap insurance
                // against odd umasks.
                TryChmod(dir, 0b111_000_000); // 0700
                return Path.Combine(dir, SocketFileName);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SocketPathResolver] XDG path {dir} unusable: {ex.Message}. Falling back to /tmp.");
            }
        }

        var uid = (int)geteuid();
        var fallback = $"/tmp/typewhisper-{uid}";

        // Defense against a pre-staged hostile directory. /tmp is world-
        // writable, so another local user can create `/tmp/typewhisper-$UID`
        // ahead of us with permissive modes and try to influence the
        // socket. If the path already exists with anything other than the
        // expected 0700 we don't trust it: try chmod first, and if a
        // subsequent verification still shows wrong bits we fall back to a
        // per-process scratch directory so we never bind inside an
        // attacker-controlled path.
        try
        {
            if (!Directory.Exists(fallback))
                Directory.CreateDirectory(fallback);
            TryChmod(fallback, 0b111_000_000); // 0700
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not prepare {fallback}: {ex.Message}");
            return CreatePrivateSocketPath(uid);
        }

        if (!IsDirectoryPrivateAndOwned(fallback, uid))
            return CreatePrivateSocketPath(uid);

        return Path.Combine(fallback, SocketFileName);
    }

    private static string CreatePrivateSocketPath(int uid)
    {
        var privatePath = Path.Combine(Path.GetTempPath(), $"typewhisper-{uid}-{Environment.ProcessId}");
        Directory.CreateDirectory(privatePath);
        TryChmod(privatePath, 0b111_000_000); // 0700
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
                Trace.WriteLine($"[SocketPathResolver] {path} not owned by uid {uid} (actual {ownerUid}).");
                return false;
            }
            return DirectoryHasExpectedMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not validate {path}: {ex.Message}");
            return false;
        }
    }

    // statx(2) ABI: kernel-defined struct, arch-independent. stx_uid is at
    // offset 20 (after stx_mask:4, stx_blksize:4, stx_attributes:8, stx_nlink:4).
    // We allocate the full 256-byte buffer the kernel writes into and read
    // only the owner uid.
    private const int StatxBufSize = 256;
    private const int StatxUidOffset = 20;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_UID = 0x00000008;

    private static bool TryGetOwnerUid(string path, out int ownerUid)
    {
        ownerUid = -1;
        var buffer = Marshal.AllocHGlobal(StatxBufSize);
        try
        {
            for (var i = 0; i < StatxBufSize; i++)
                Marshal.WriteByte(buffer, i, 0);

            var rc = statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_UID, buffer);
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
            const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            return (mode & forbidden) == 0 && (mode & expected) == expected;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not stat {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Best-effort <c>chmod</c>; logs on failure but never throws.</summary>
    public static void TryChmod(string path, uint mode)
    {
        try
        {
            var rc = chmod(path, mode);
            if (rc != 0)
                Trace.WriteLine($"[SocketPathResolver] chmod({path}, 0{Convert.ToString(mode, 8)}) returned {rc}.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] chmod({path}) threw: {ex.Message}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, IntPtr statxbuf);
}
