using System;
using System.Text.Json;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux.Cli.Commands;

/// <summary>
/// Thin client for <c>typewhisper record &lt;verb&gt;</c>. Sends one JSON
/// request to the running instance, prints the JSON response to stdout, and
/// returns an exit code that downstream scripts can branch on.
/// </summary>
/// <remarks>
/// Exit codes:
/// <list type="bullet">
///   <item>0 — server responded <c>ok:true</c>.</item>
///   <item>1 — server responded <c>ok:false</c> (e.g. unknown verb on an older instance).</item>
///   <item>2 — no running instance, or the socket call failed (reported on stderr).</item>
/// </list>
/// Compositor binds (Hyprland <c>bind</c>/<c>bindr</c>, Sway press/release)
/// expect a fire-and-forget exec that doesn't block; this command sends one
/// short line and closes the socket synchronously to keep latency in the
/// low-millisecond range.
/// </remarks>
internal static class RecordCommand
{
    public static int Run(string verb)
    {
        var cmd = verb switch
        {
            "start"  => JsonControlProtocol.CmdRecordStart,
            "stop"   => JsonControlProtocol.CmdRecordStop,
            "toggle" => JsonControlProtocol.CmdRecordToggle,
            "cancel" => JsonControlProtocol.CmdRecordCancel,
            _ => null,
        };
        if (cmd is null)
        {
            Console.Error.WriteLine($"typewhisper: unknown record verb '{verb}'");
            return 2;
        }

        var path = SocketPathResolver.ResolveControlSocketPath();
        var request = new { v = JsonControlProtocol.CurrentVersion, cmd = cmd };

        if (!ControlSocketClient.TrySendJson(path, request, out var responseJson, out var error))
        {
            // No socket file OR transport failure. Both look the same to a
            // shell-script caller — "the app isn't accepting input" — so we
            // collapse to a single message and exit 2.
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
