using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
///     Resolves the path to the TypeWhisper IPC control socket and ensures the
///     containing directory exists with user-only permissions.
/// </summary>
/// <remarks>
///     Preferred: <c>$XDG_RUNTIME_DIR/typewhisper/control.sock</c> (runtime dir is
///     already 0700 via systemd-logind). Falls back to
///     <c>TypeWhisperEnvironment.BasePath/Runtime/control.sock</c> with an explicit
///     chmod 0700 when <c>XDG_RUNTIME_DIR</c> is absent or unusable.
/// </remarks>
internal static partial class SocketPathResolver
{
    private const string SocketFileName = "control.sock";

    internal static string DefaultFallbackDirectory =>
        Path.Join(TypeWhisperEnvironment.BasePath, "Runtime");

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
        return ResolveControlSocketPath(DefaultFallbackDirectory);
    }

    internal static string ResolveControlSocketPath(string fallbackDirectory)
    {
        var uid = (int)geteuid();
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
        {
            var dir = Path.Join(xdg, "typewhisper");
            try
            {
                PreparePrivateDirectory(dir, uid);
                return Path.Join(dir, SocketFileName);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[SocketPathResolver] XDG path {dir} unusable: {ex.Message}. Falling back to {fallbackDirectory}."
                );
            }
        }

        PreparePrivateDirectory(fallbackDirectory, uid);
        Trace.WriteLine(
            $"[SocketPathResolver] Using user-data socket directory {fallbackDirectory}."
        );
        return Path.Join(fallbackDirectory, SocketFileName);
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

    private static void PreparePrivateDirectory(string directory, int uid)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Could not create private control socket directory {directory}.",
                ex
            );
        }

        TryChmod(directory, 0b111_000_000); // 0700
        if (!IsDirectoryPrivateAndOwned(directory, uid))
        {
            throw new IOException(
                $"Could not secure private control socket directory {directory} with owner-only mode 0700."
            );
        }
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

            if (ownerUid == uid)
            {
                return DirectoryHasExpectedMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            Trace.WriteLine(
                $"[SocketPathResolver] {path} not owned by uid {uid} (actual {ownerUid})."
            );
            return false;
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

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial uint geteuid();

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chmod(string path, uint mode);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        IntPtr statxbuf
    );
}
