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
        if (!Directory.Exists(fallback))
            Directory.CreateDirectory(fallback);
        TryChmod(fallback, 0b111_000_000); // 0700

        if (!DirectoryHasExpectedMode(fallback, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            // The directory wasn't ours to begin with (someone else owns
            // it) or the mode can't be tightened. Refuse to share that
            // path; use a private subdirectory inside our process scratch
            // so we still get an IPC endpoint without trusting hostile
            // state. The fallback-fallback path is intentionally not the
            // shape that any other tool would predict.
            var privatePath = Path.Combine(Path.GetTempPath(), $"typewhisper-{uid}-{Environment.ProcessId}");
            Directory.CreateDirectory(privatePath);
            TryChmod(privatePath, 0b111_000_000); // 0700
            Trace.WriteLine($"[SocketPathResolver] {fallback} is not 0700-private; falling back to {privatePath}.");
            return Path.Combine(privatePath, SocketFileName);
        }

        return Path.Combine(fallback, SocketFileName);
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
}
