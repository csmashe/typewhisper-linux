using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Ipc;

internal enum ApiSocketCleanupResult
{
    Missing,
    Removed,
    Live,
    Indeterminate,
    OwnershipContended,
}

/// <summary>
///     Owns the stable advisory lock that serializes every API-socket bind and unlink.
///     The API lock is distinct from the live control socket's lock.
/// </summary>
internal sealed partial class ApiSocketOwnership : IDisposable
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

    private ApiSocketOwnership(string socketPath, string lockPath, SafeFileHandle lockHandle)
    {
        SocketPath = socketPath;
        LockPath = lockPath;
        _lockHandle = lockHandle;
    }

    private string SocketPath { get; }

    internal string LockPath { get; }

    internal static bool TryAcquire(
        string socketPath,
        [NotNullWhen(true)] out ApiSocketOwnership? ownership
    )
    {
        ownership = null;
        var lockPath = Path.Join(Path.GetDirectoryName(socketPath)!, "api.lock");
        // ReSharper disable once SuggestVarOrType_SimpleTypes -- nullable enables ownership transfer below.
        SafeFileHandle? handle = OpenLockFile(lockPath);
        try
        {
            SetOwnerOnlyMode(handle, lockPath);
            while (flock(handle, LockExclusive | LockNonBlocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                // ReSharper disable once ConvertIfStatementToSwitchStatement -- independent errno guard clauses; a switch would hide that the fallthrough throws.
                if (error == ErrorInterrupted)
                {
                    continue;
                }

                if (error == ErrorTryAgain)
                {
                    return false;
                }

                throw new IOException(
                    $"Could not acquire API socket ownership lock {lockPath}.",
                    new Win32Exception(error)
                );
            }

            ownership = new ApiSocketOwnership(socketPath, lockPath, handle);
            handle = null;
            return true;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static ApiSocketCleanupResult TryCleanupStaleSocket(string socketPath)
    {
        try
        {
            if (!TryAcquire(socketPath, out var ownership))
            {
                return ApiSocketCleanupResult.OwnershipContended;
            }

            using (ownership)
            {
                return ownership.CleanupStaleSocket();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ApiSocketOwnership] Could not acquire cleanup ownership for {socketPath}: {ex.Message}"
            );
            return ApiSocketCleanupResult.Indeterminate;
        }
    }

    /// <summary>
    ///     Re-probes and, only on ECONNREFUSED, unlinks a stale socket while ownership is held.
    /// </summary>
    internal ApiSocketCleanupResult CleanupStaleSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!File.Exists(SocketPath))
        {
            return ApiSocketCleanupResult.Missing;
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
            return ApiSocketCleanupResult.Live;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            if (!File.Exists(SocketPath))
            {
                return ApiSocketCleanupResult.Missing;
            }

            try
            {
                File.Delete(SocketPath);
                if (!File.Exists(SocketPath))
                {
                    Trace.WriteLine($"[ApiSocketOwnership] Removed stale socket at {SocketPath}.");
                    return ApiSocketCleanupResult.Removed;
                }
            }
            catch (Exception deleteException)
            {
                Trace.WriteLine(
                    $"[ApiSocketOwnership] Failed to remove stale socket {SocketPath}: {deleteException.Message}"
                );
                return ApiSocketCleanupResult.Indeterminate;
            }

            Trace.WriteLine(
                $"[ApiSocketOwnership] Stale socket {SocketPath} remained after deletion."
            );
            return ApiSocketCleanupResult.Indeterminate;
        }
        catch (Exception ex)
        {
            if (!File.Exists(SocketPath))
            {
                return ApiSocketCleanupResult.Missing;
            }

            Trace.WriteLine(
                $"[ApiSocketOwnership] Probe of {SocketPath} was indeterminate: {ex.Message}"
            );
            return ApiSocketCleanupResult.Indeterminate;
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
                return new SafeFileHandle(fd, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorInterrupted)
            {
                continue;
            }

            throw new IOException(
                $"Could not open API socket ownership lock {lockPath}.",
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
                $"Could not secure API socket ownership lock {lockPath} with mode 0600.",
                new Win32Exception(error)
            );
        }
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(SafeFileHandle fd, int operation);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int fchmod(SafeFileHandle fd, uint mode);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags, uint mode);
}
