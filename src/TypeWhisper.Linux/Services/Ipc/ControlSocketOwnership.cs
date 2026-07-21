using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Ipc;

internal enum ControlSocketCleanupResult
{
    Missing,
    Removed,
    Live,
    Indeterminate,
    OwnershipContended
}

/// <summary>
///     Owns the stable advisory lock that serializes every control-socket bind and unlink.
///     The lockfile is persistent; closing its file descriptor releases ownership without
///     replacing the inode that all contenders lock.
/// </summary>
internal sealed partial class ControlSocketOwnership : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrorInterrupted = 4;
    private const int ErrorTryAgain = 11;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint OwnerReadWriteMode = 0b110_000_000; // 0600
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);

    private readonly SafeFileHandle _lockHandle;
    private int _disposed;

    private ControlSocketOwnership(
        string socketPath,
        string lockPath,
        SafeFileHandle lockHandle
    )
    {
        SocketPath = socketPath;
        LockPath = lockPath;
        _lockHandle = lockHandle;
    }

    private string SocketPath { get; }

    internal string LockPath { get; }

    /// <summary>
    ///     Opens the persistent lockfile and attempts an exclusive lock without blocking.
    ///     Returns false only for ordinary lock contention; other failures are reported.
    /// </summary>
    internal static bool TryAcquire(
        string socketPath,
        [NotNullWhen(true)] out ControlSocketOwnership? ownership
    )
    {
        ownership = null;
        var lockPath = Path.Join(Path.GetDirectoryName(socketPath)!, "control.lock");
        // ReSharper disable once SuggestVarOrType_SimpleTypes -- OpenLockFile returns a non-nullable handle; the explicit nullable type is required for the `handle = null` ownership transfer below.
        SafeFileHandle? handle = OpenLockFile(lockPath);
        try
        {
            SetOwnerOnlyMode(handle, lockPath);
            while (flock(handle, LockExclusive | LockNonBlocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                // ReSharper disable once ConvertIfStatementToSwitchStatement -- errno guard chain inside the retry loop; a switch would obscure the continue/return/throw split.
                if (error == ErrorInterrupted)
                {
                    continue;
                }

                if (error == ErrorTryAgain)
                {
                    return false;
                }

                throw new IOException(
                    $"Could not acquire control socket ownership lock {lockPath}.",
                    new Win32Exception(error)
                );
            }

            ownership = new ControlSocketOwnership(socketPath, lockPath, handle);
            handle = null;
            return true;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    ///     Best-effort client cleanup. Contention or any acquisition/probe/delete failure
    ///     leaves the socket pathname untouched.
    /// </summary>
    internal static ControlSocketCleanupResult TryCleanupStaleSocket(string socketPath)
    {
        try
        {
            if (!TryAcquire(socketPath, out var ownership))
            {
                return ControlSocketCleanupResult.OwnershipContended;
            }

            using (ownership)
            {
                return ownership.CleanupStaleSocket();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ControlSocketOwnership] Could not acquire cleanup ownership for {socketPath}: {ex.Message}"
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
    }

    /// <summary>
    ///     Re-probes and, only on ECONNREFUSED, unlinks a stale socket while ownership is held.
    /// </summary>
    internal ControlSocketCleanupResult CleanupStaleSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!File.Exists(SocketPath))
        {
            return ControlSocketCleanupResult.Missing;
        }

        try
        {
            using var probe = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            using var timeout = new CancellationTokenSource(s_probeTimeout);
            probe
                .ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return ControlSocketCleanupResult.Live;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            if (!File.Exists(SocketPath))
            {
                return ControlSocketCleanupResult.Missing;
            }

            try
            {
                File.Delete(SocketPath);
                if (!File.Exists(SocketPath))
                {
                    Trace.WriteLine(
                        $"[ControlSocketOwnership] Removed stale socket at {SocketPath}."
                    );
                    return ControlSocketCleanupResult.Removed;
                }
            }
            catch (Exception deleteException)
            {
                Trace.WriteLine(
                    $"[ControlSocketOwnership] Failed to remove stale socket {SocketPath}: {deleteException.Message}"
                );
                return ControlSocketCleanupResult.Indeterminate;
            }

            Trace.WriteLine(
                $"[ControlSocketOwnership] Stale socket {SocketPath} remained after deletion."
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
        catch (Exception ex)
        {
            if (!File.Exists(SocketPath))
            {
                return ControlSocketCleanupResult.Missing;
            }

            Trace.WriteLine(
                $"[ControlSocketOwnership] Probe of {SocketPath} was indeterminate: {ex.Message}"
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Closing releases flock ownership. Never unlink the stable lockfile.
        _lockHandle.Dispose();
    }

    private static SafeFileHandle OpenLockFile(string lockPath)
    {
        while (true)
        {
            var fd = open(
                lockPath,
                OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec,
                OwnerReadWriteMode
            );
            if (fd >= 0)
            {
                // Native open has no managed sharing policy, so every contender reaches
                // the explicit nonblocking flock below.
                return new SafeFileHandle(fd, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorInterrupted)
            {
                continue;
            }

            throw new IOException(
                $"Could not open control socket ownership lock {lockPath}.",
                new Win32Exception(error)
            );
        }
    }

    private static void SetOwnerOnlyMode(SafeFileHandle handle, string lockPath)
    {
        while (fchmod(handle, OwnerReadWriteMode) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorInterrupted)
            {
                continue;
            }

            throw new IOException(
                $"Could not secure control socket ownership lock {lockPath} with mode 0600.",
                new Win32Exception(error)
            );
        }
    }

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(SafeFileHandle fd, int operation);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int fchmod(SafeFileHandle fd, uint mode);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags, uint mode);
}
