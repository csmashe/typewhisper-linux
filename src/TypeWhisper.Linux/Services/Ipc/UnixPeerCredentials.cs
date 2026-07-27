using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>Reads Linux peer credentials before a Unix-socket connection reaches HTTP.</summary>
internal static partial class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    internal static bool IsOwnedByEffectiveUser(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var credentials = Get(socket);
        return credentials.Uid == geteuid();
    }

    private static PeerCredentials Get(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var credentials = new PeerCredentials();
        var length = (uint)Marshal.SizeOf<PeerCredentials>();
        if (
            getsockopt(
                socket.Handle,
                SolSocket,
                SoPeerCred,
                ref credentials,
                ref length
            ) != 0
        )
        {
            throw new IOException(
                "Could not read Unix-socket peer credentials.",
                new Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        // ReSharper disable once ConvertIfStatementToReturnStatement -- keeps the two size/errno guards in the same throw-on-failure shape; the suggested ternary-throw does not.
        if (length != Marshal.SizeOf<PeerCredentials>())
        {
            throw new IOException("Unix-socket peer credentials had an unexpected size.");
        }

        return credentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(
        IntPtr socket,
        int level,
        int optionName,
        ref PeerCredentials optionValue,
        ref uint optionLength
    );

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial uint geteuid();
}
