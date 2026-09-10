using System.Net.Sockets;

namespace TypeWhisper.Cli.Tests;

/// <summary>
///     Socket errors a stub can legitimately see while a test tears it down. Every other
///     socket error is a real fixture failure, so DisposeAsync must let it surface.
/// </summary>
internal static class SocketShutdown
{
    internal static bool IsShutdownError(SocketException ex)
    {
        return ex.SocketErrorCode
            is SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            or SocketError.Shutdown
            or SocketError.OperationAborted
            or SocketError.Interrupted;
    }
}
