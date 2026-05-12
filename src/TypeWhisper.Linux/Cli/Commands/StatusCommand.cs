using System;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux.Cli.Commands;

/// <summary>
/// Thin client for <c>typewhisper status</c>. Prints the server's JSON
/// status line to stdout. Useful for shell completions, monitoring scripts,
/// or quickly verifying which hotkey backend is active.
/// </summary>
internal static class StatusCommand
{
    public static int Run()
    {
        var path = SocketPathResolver.ResolveControlSocketPath();
        var request = new { v = JsonControlProtocol.CurrentVersion, cmd = JsonControlProtocol.CmdStatus };

        if (!ControlSocketClient.TrySendJson(path, request, out var responseJson, out var error))
        {
            Console.Error.WriteLine(error is null
                ? "typewhisper: not running"
                : $"typewhisper: {error}");
            return 2;
        }

        Console.WriteLine(responseJson);
        return responseJson.Contains("\"ok\":true") ? 0 : 1;
    }
}
