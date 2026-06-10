using System.Text.Json;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux.Cli.Commands;

/// <summary>
///     Thin client for <c>typewhisper record &lt;verb&gt;</c>. Sends one JSON request to the
///     running instance, prints the JSON response to stdout, and returns an exit code.
///     Exit codes: 0 = ok:true, 1 = ok:false (unknown verb etc.), 2 = no instance / socket error.
///     Closes the socket synchronously so compositor binds (Hyprland bind/bindr, Sway) don't block.
/// </summary>
internal static class RecordCommand
{
    public static int Run(string verb)
    {
        var cmd = verb switch
        {
            "start" => JsonControlProtocol.CmdRecordStart,
            "stop" => JsonControlProtocol.CmdRecordStop,
            "toggle" => JsonControlProtocol.CmdRecordToggle,
            "cancel" => JsonControlProtocol.CmdRecordCancel,
            _ => null
        };
        if (cmd is null)
        {
            Console.Error.WriteLine($"typewhisper: unknown record verb '{verb}'");
            return 2;
        }

        var path = SocketPathResolver.ResolveControlSocketPath();
        var request = new { v = JsonControlProtocol.CurrentVersion, cmd };

        if (!ControlSocketClient.TrySendJson(path, request, out var responseJson, out var error))
        {
            // No socket file and transport failure look identical to a script caller, so collapse to exit 2.
            Console.Error.WriteLine(
                error is null ? "typewhisper: not running" : $"typewhisper: {error}"
            );
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
            // Explicit kind check prevents GetBoolean() throwing on a non-boolean "ok" from a misbehaving server.
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