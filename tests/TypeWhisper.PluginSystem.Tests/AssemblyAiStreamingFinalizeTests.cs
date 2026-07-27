using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AssemblyAiStreamingFinalizeTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Finalize_FlushesSubMinimumResidualPaddedToSilenceBeforeTerminate()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await WebSocketSessionPump
            .StartConnectedAsync(
                new AssemblyAiWebSocketAdapter("assembly-key", "en"),
                transport,
                CancellationToken.None
            )
            .WaitAsync(s_timeout);
        var residual = Enumerable
            .Range(0, 1599)
            .Select(index => (byte)(index % 251))
            .ToArray();

        await pump.SendAudioAsync(residual, CancellationToken.None).WaitAsync(s_timeout);
        Assert.Empty(transport.DrainSent());

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var tail = await transport.NextSentAsync();
        var terminate = await transport.NextSentAsync();

        // Tail is padded to the provider minimum with silence so AssemblyAI accepts it.
        var tailPayload = tail.Payload.ToArray();
        Assert.Equal(WebSocketMessageType.Binary, tail.MessageType);
        Assert.Equal(AssemblyAiWebSocketAdapter.MinimumChunkBytes, tailPayload.Length);
        Assert.Equal(residual, tailPayload[..residual.Length]);
        Assert.All(tailPayload[residual.Length..], padding => Assert.Equal(0, padding));
        Assert.Equal(WebSocketMessageType.Text, terminate.MessageType);
        Assert.Equal(
            """{"type":"Terminate"}""",
            Encoding.UTF8.GetString(terminate.Payload.Span)
        );
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText(
            """{"type":"Termination","audio_duration_seconds":0.05}"""
        );
        await finalize.WaitAsync(s_timeout);
    }
}
