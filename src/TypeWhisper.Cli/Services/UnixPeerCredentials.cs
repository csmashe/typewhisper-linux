using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TypeWhisper.Cli.Services;

/// <summary>Validates the server identity before HTTP headers or bodies are sent.</summary>
internal static class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    internal static bool IsOwnedByEffectiveUser(Socket socket)
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
                "Could not read TypeWhisper API peer credentials.",
                new Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        if (length != Marshal.SizeOf<PeerCredentials>())
        {
            throw new IOException("TypeWhisper API peer credentials had an unexpected size.");
        }

        return credentials.Uid == geteuid();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    // Classic DllImport keeps the CLI project free of generated unsafe marshalling code.
#pragma warning disable SYSLIB1054
    // ReSharper disable once InconsistentNaming -- native libc function name.
    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(
        IntPtr socket,
        int level,
        int optionName,
        ref PeerCredentials optionValue,
        ref uint optionLength
    );

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
#pragma warning restore SYSLIB1054
}
