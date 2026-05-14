using System;
using System.Text.Json;
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
        return IsOk(responseJson) ? 0 : 1;
    }

    private static bool IsOk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("ok", out var ok)
                   && (ok.ValueKind == JsonValueKind.True || ok.ValueKind == JsonValueKind.False)
                   && ok.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
