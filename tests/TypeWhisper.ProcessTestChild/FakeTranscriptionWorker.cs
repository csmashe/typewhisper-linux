using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeWhisper.ProcessTestChild;

/// <summary>
///     Speaks the transcription worker protocol badly on purpose, so tests can watch how the host
///     reacts to frames a real worker never sends. Written against the wire format, not the host's
///     types, as a foreign or broken worker would be.
/// </summary>
internal static class FakeTranscriptionWorker
{
    internal static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var behavior = args[1];
        if (behavior == "exit-before-connect")
        {
            return 7;
        }

        var socketPath = args[args.ToList().IndexOf("--socket") + 1];
        var token = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIPTION_WORKER_TOKEN") ?? "";
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(socket);

        await WriteAsync(stream, new JsonObject
        {
            ["type"] = "hello",
            ["protocolVersion"] = 1,
            ["processId"] = Environment.ProcessId,
            ["token"] = behavior == "wrong-token" ? "not-the-token" : token,
        });

        while (await ReadIdAsync(stream) is { } id)
        {
            switch (behavior)
            {
                case "oversized-header":
                    await WritePrefixAsync(stream, (64 << 20) + 1, 0);
                    break;
                case "payload":
                    await WriteAsync(stream, Response(id), payload: [1, 2, 3]);
                    break;
                case "garbage-header":
                    var garbage = "not json"u8.ToArray();
                    await WritePrefixAsync(stream, garbage.Length, 0);
                    await stream.WriteAsync(garbage);
                    break;
                case "stale-then-answer":
                    await WriteAsync(stream, Response(id + 1000, "stale"));
                    await WriteAsync(stream, Response(id));
                    break;
                default:
                    await WriteAsync(stream, Response(id));
                    break;
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static JsonObject Response(long id, string text = "fake")
    {
        return new JsonObject
        {
            ["type"] = "response",
            ["id"] = id,
            ["result"] = new JsonObject { ["text"] = text, ["durationSeconds"] = 1.0 },
        };
    }

    private static async Task<long?> ReadIdAsync(Stream stream)
    {
        var prefix = new byte[8];
        if (await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false) < prefix.Length)
        {
            return null;
        }

        var header = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
        await stream.ReadExactlyAsync(header);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4))];
        await stream.ReadExactlyAsync(payload);
        using var document = JsonDocument.Parse(header);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task WriteAsync(Stream stream, JsonObject message, byte[]? payload = null)
    {
        var header = Encoding.UTF8.GetBytes(message.ToJsonString());
        await WritePrefixAsync(stream, header.Length, payload?.Length ?? 0);
        await stream.WriteAsync(header);
        if (payload is not null)
        {
            await stream.WriteAsync(payload);
        }

        await stream.FlushAsync();
    }

    private static async Task WritePrefixAsync(Stream stream, int headerLength, int payloadLength)
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, headerLength);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), payloadLength);
        await stream.WriteAsync(prefix);
        await stream.FlushAsync();
    }
}
