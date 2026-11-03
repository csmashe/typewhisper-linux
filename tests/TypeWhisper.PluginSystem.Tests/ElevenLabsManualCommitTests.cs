using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ElevenLabsManualCommitTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LoudAudioAfterTwentySeconds_CommitsOnlyAfterQuietRun()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        await pump.SendAudioAsync(CreateLoudAudio(640000), CancellationToken.None).WaitAsync(s_timeout);
        var loudMessages = transport.DrainSent().Select(ParseAudio).ToArray();
        Assert.Equal(640000, loudMessages.Sum(message => message.Audio.Length));
        Assert.All(loudMessages, message => Assert.False(message.Commit));

        await pump.SendAudioAsync(new byte[6400], CancellationToken.None).WaitAsync(s_timeout);
        var pause = ParseAudio(Assert.Single(transport.DrainSent()));
        Assert.True(pause.Commit);
        Assert.Equal(6400, pause.Audio.Length);
    }

    [Fact]
    public async Task PauseEndingInsideChunk_CommitsAtThePauseAndKeepsFollowingSpeechUncommitted()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        await pump.SendAudioAsync(CreateLoudAudio(640000), CancellationToken.None).WaitAsync(s_timeout);
        transport.DrainSent();

        // 200 ms of silence followed by 800 ms of speech inside one 1 s chunk.
        var chunk = new byte[32000];
        CreateLoudAudio(25600).CopyTo(chunk, 6400);
        await pump.SendAudioAsync(chunk, CancellationToken.None).WaitAsync(s_timeout);
        var messages = transport.DrainSent().Select(ParseAudio).ToArray();

        Assert.Equal(2, messages.Length);
        Assert.True(messages[0].Commit);
        Assert.Equal(new byte[6400], messages[0].Audio);
        Assert.False(messages[1].Commit);
        Assert.Equal(chunk[6400..], messages[1].Audio);
    }

    [Fact]
    public async Task Silence_CommitsAtTwentySecondsWithBoundedChunks()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
        var messages = transport.DrainSent().Select(ParseAudio).ToArray();
        Assert.Equal(20, messages.Length);
        var sentBytes = 0;
        foreach (var message in messages)
        {
            Assert.InRange(message.Audio.Length, 3200, 32000);
            sentBytes += message.Audio.Length;
            Assert.Equal(sentBytes == 640000, message.Commit);
        }
        Assert.Equal(640000, sentBytes);
    }

    [Fact]
    public async Task PeriodicCommit_ResetsCountersWithoutWaitingForAcknowledgement()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        for (var round = 0; round < 2; round++)
        {
            await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
            var messages = transport.DrainSent().Select(ParseAudio).ToArray();
            Assert.Equal(640000, messages.Sum(message => message.Audio.Length));
            Assert.Single(messages, message => message.Commit);
            Assert.True(messages[^1].Commit);
        }
    }

    [Fact]
    public async Task ContinuousSpeechAtThirtyTwoSeconds_FaultsPumpWithoutCommitting()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => pump.SendAudioAsync(CreateLoudAudio(1024000), CancellationToken.None).WaitAsync(s_timeout)
        );
        Assert.Equal(
            "ElevenLabs live transcription needs a pause after 32 s of continuous speech; retrying with the complete recording.",
            exception.Message
        );
        Assert.All(transport.DrainSent().Select(ParseAudio), message => Assert.False(message.Commit));
        Assert.Same(exception, pump.Fault);
        var subsequent = await Assert.ThrowsAsync<IOException>(
            () => pump.SendAudioAsync(new byte[3200], CancellationToken.None).WaitAsync(s_timeout)
        );
        Assert.Same(exception, subsequent);
    }

    [Fact]
    public async Task Finalize_CommitsSubMinimumTailAndWaitsForTranscript()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var residual = Enumerable.Range(0, 1599).Select(index => (byte)(index % 251)).ToArray();

        await pump.SendAudioAsync(residual, CancellationToken.None).WaitAsync(s_timeout);
        Assert.Empty(transport.DrainSent());

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var tail = ParseAudio(await transport.NextSentAsync());
        Assert.True(tail.Commit);
        Assert.Equal(residual, tail.Audio);
        Assert.Empty(transport.DrainSent());
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":"tail"}""");
        await finalize.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task IdenticalPeriodicCommits_SuppressOnlyEachTimestampedVariant()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var finals = new ConcurrentQueue<StreamingTranscriptEvent>();
        pump.TranscriptReceived += transcript =>
        {
            if (transcript.IsFinal)
                finals.Enqueue(transcript);
        };

        for (var round = 0; round < 2; round++)
        {
            await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
            Assert.Single(transport.DrainSent().Select(ParseAudio), message => message.Commit);
            transport.EnqueueText("""{"message_type":"committed_transcript","text":"Ja."}""");
            transport.EnqueueText("""{"message_type":"committed_transcript_with_timestamps","text":"Ja."}""");
            await WaitForReceiveLoopAsync(pump, transport);
            Assert.Equal(round + 1, finals.Count);
        }

        Assert.Equal(2, finals.Count);
        Assert.All(finals, transcript => Assert.Equal(new StreamingTranscriptEvent("Ja.", true), transcript));
        Assert.Null(pump.Fault);
    }

    [Fact]
    public async Task PeriodicCommitAcknowledgedAfterFinalizeStarts_DoesNotCompleteFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var finals = new ConcurrentQueue<string>();
        pump.TranscriptReceived += transcript =>
        {
            if (transcript.IsFinal)
                finals.Enqueue(transcript.Text);
        };

        await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
        Assert.True(ParseAudio(transport.DrainSent()[^1]).Commit);
        await pump.SendAudioAsync(new byte[3200], CancellationToken.None).WaitAsync(s_timeout);
        Assert.False(ParseAudio(Assert.Single(transport.DrainSent())).Commit);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var tail = ParseAudio(await transport.NextSentAsync());
        Assert.True(tail.Commit);
        Assert.Empty(tail.Audio);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":"first"}""");
        transport.EnqueueText("""{"message_type":"committed_transcript_with_timestamps","text":"first"}""");
        await WaitForReceiveLoopAsync(pump, transport);
        Assert.False(finalize.IsCompleted);
        Assert.Equal(["first"], finals);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":"tail"}""");
        await finalize.WaitAsync(s_timeout);
        Assert.Equal(["first", "tail"], finals);
        Assert.Null(pump.Fault);
    }

    [Fact]
    public async Task TimestampedDuplicateOfEarlierCommitAfterFinalize_DoesNotCompleteFinalize()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var finals = new ConcurrentQueue<string>();
        pump.TranscriptReceived += transcript =>
        {
            if (transcript.IsFinal)
                finals.Enqueue(transcript.Text);
        };

        await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
        Assert.True(ParseAudio(transport.DrainSent()[^1]).Commit);
        transport.EnqueueText("""{"message_type":"committed_transcript","text":"first"}""");
        await WaitForReceiveLoopAsync(pump, transport);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        Assert.True(ParseAudio(await transport.NextSentAsync()).Commit);

        transport.EnqueueText("""{"message_type":"committed_transcript_with_timestamps","text":"first"}""");
        await WaitForReceiveLoopAsync(pump, transport);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":"tail"}""");
        await finalize.WaitAsync(s_timeout);
        Assert.Equal(["first", "tail"], finals);
        Assert.Null(pump.Fault);
    }

    [Fact]
    public async Task SilentPeriodicCommitAcknowledgedAfterFinalize_CountsBothEmptyVariantsOnce()
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);

        await pump.SendAudioAsync(new byte[640000], CancellationToken.None).WaitAsync(s_timeout);
        Assert.True(ParseAudio(transport.DrainSent()[^1]).Commit);

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        Assert.True(ParseAudio(await transport.NextSentAsync()).Commit);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":""}""");
        transport.EnqueueText("""{"message_type":"committed_transcript_with_timestamps","text":""}""");
        await WaitForReceiveLoopAsync(pump, transport);
        Assert.False(finalize.IsCompleted);

        transport.EnqueueText("""{"message_type":"committed_transcript","text":""}""");
        await finalize.WaitAsync(s_timeout);
        Assert.Null(pump.Fault);
    }

    [Theory]
    [InlineData(100000)]
    [InlineData(100001)]
    public async Task LargeHostChunk_SplitsAndPreservesAudioIncludingOddTrailingByte(int byteCount)
    {
        var transport = new ScriptedWebSocketTransport();
        await using var pump = await StartAsync(transport);
        var audio = Enumerable.Range(0, byteCount).Select(index => (byte)(index % 251)).ToArray();

        await pump.SendAudioAsync(audio, CancellationToken.None).WaitAsync(s_timeout);
        var messages = transport.DrainSent().Select(ParseAudio).ToArray();

        var sentBytes = byteCount - byteCount % 2;
        int[] expectedLengths = [32000, 32000, 32000, sentBytes - 96000];
        Assert.Equal(expectedLengths, messages.Select(message => message.Audio.Length));
        Assert.Equal(audio[..sentBytes], messages.SelectMany(message => message.Audio).ToArray());
        Assert.All(messages, message => Assert.False(message.Commit));

        // An odd trailing byte stays buffered until the tail commit carries it.
        var finalize = pump.FinalizeAsync(CancellationToken.None);
        var tail = ParseAudio(await transport.NextSentAsync().WaitAsync(s_timeout));
        Assert.True(tail.Commit);
        Assert.Equal(audio[sentBytes..], tail.Audio);
        transport.EnqueueText("""{"message_type":"committed_transcript","text":"tail"}""");
        await finalize.WaitAsync(s_timeout);
    }

    private static Task<WebSocketSessionPump> StartAsync(ScriptedWebSocketTransport transport) =>
        WebSocketSessionPump.StartConnectedAsync(
            new ElevenLabsWebSocketAdapter("eleven-key", "scribe_v2_realtime", null, noVerbatim: true),
            transport,
            CancellationToken.None
        ).WaitAsync(s_timeout);

    private static byte[] CreateLoudAudio(int byteCount)
    {
        var audio = new byte[byteCount];
        var samples = MemoryMarshal.Cast<byte, short>(audio.AsSpan());
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (short)(index % 2 == 0 ? 1000 : -1000);
        return audio;
    }

    private static (bool Commit, byte[] Audio) ParseAudio(WebSocketOutboundMessage message)
    {
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        Assert.Equal("input_audio_chunk", root.GetProperty("message_type").GetString());
        return (root.GetProperty("commit").GetBoolean(), root.GetProperty("audio_base_64").GetBytesFromBase64());
    }

    private static async Task WaitForReceiveLoopAsync(
        WebSocketSessionPump pump,
        ScriptedWebSocketTransport transport
    )
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.TranscriptReceived += OnTranscript;
        try
        {
            // A later partial proves both committed variants have passed through the receive loop.
            transport.EnqueueText("""{"message_type":"partial_transcript","text":"barrier"}""");
            await received.Task.WaitAsync(s_timeout);
        }
        finally
        {
            pump.TranscriptReceived -= OnTranscript;
        }

        return;

        void OnTranscript(StreamingTranscriptEvent transcript)
        {
            if (transcript is { IsFinal: false, Text: "barrier" })
                received.TrySetResult();
        }
    }
}
