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

    [Fact]
    public async Task SendAudio_SplitsBufferedResidualPlusFullSecondWithinProviderFrameLimit()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await WebSocketSessionPump
            .StartConnectedAsync(
                new AssemblyAiWebSocketAdapter("assembly-key", "en"),
                transport,
                CancellationToken.None
            )
            .WaitAsync(s_timeout);
        var audio = Enumerable
            .Range(0, 1260 + AssemblyAiWebSocketAdapter.MaximumChunkBytes)
            .Select(index => (byte)(index % 251))
            .ToArray();

        // 39.375 ms buffers below the minimum, then exactly 1000 ms arrives.
        await pump.SendAudioAsync(audio.AsMemory(0, 1260), CancellationToken.None).WaitAsync(s_timeout);
        Assert.Empty(transport.DrainSent());
        await pump.SendAudioAsync(audio.AsMemory(1260), CancellationToken.None).WaitAsync(s_timeout);

        var first = await transport.NextSentAsync();
        Assert.Equal(WebSocketMessageType.Binary, first.MessageType);
        Assert.Equal(AssemblyAiWebSocketAdapter.MaximumChunkBytes, first.Payload.Length);
        Assert.Equal(audio[..AssemblyAiWebSocketAdapter.MaximumChunkBytes], first.Payload.ToArray());
        Assert.Empty(transport.DrainSent());

        // The 1260-byte remainder stays buffered and is padded on finalize.
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var tail = (await transport.NextSentAsync()).Payload.ToArray();
        Assert.Equal(AssemblyAiWebSocketAdapter.MinimumChunkBytes, tail.Length);
        Assert.Equal(audio[AssemblyAiWebSocketAdapter.MaximumChunkBytes..], tail[..1260]);
        Assert.Equal(WebSocketMessageType.Text, (await transport.NextSentAsync()).MessageType);

        transport.EnqueueText(
            """{"type":"Termination","audio_duration_seconds":1.04}"""
        );
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task SendAudio_SplitsLongWriteIntoFramesWithinProviderBounds()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await WebSocketSessionPump
            .StartConnectedAsync(
                new AssemblyAiWebSocketAdapter("assembly-key", "en"),
                transport,
                CancellationToken.None
            )
            .WaitAsync(s_timeout);
        var audio = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 251)).ToArray();

        await pump.SendAudioAsync(audio, CancellationToken.None).WaitAsync(s_timeout);

        var sent = new List<byte>();
        sent.AddRange((await transport.NextSentAsync()).Payload.ToArray());
        sent.AddRange((await transport.NextSentAsync()).Payload.ToArray());
        sent.AddRange((await transport.NextSentAsync()).Payload.ToArray());
        Assert.Empty(transport.DrainSent());
        // 32000 + 32000 + 6000: every frame is within 50-1000 ms and nothing is lost.
        Assert.Equal(audio, sent.ToArray());
    }
}
