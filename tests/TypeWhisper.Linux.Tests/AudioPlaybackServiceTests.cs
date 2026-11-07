using TypeWhisper.Core;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioPlaybackServiceTests
{
    [Fact]
    public void Idle_service_holds_no_PortAudio_reference()
    {
        var initializations = 0;
        var terminations = 0;

        var playback = new AudioPlaybackService(() => initializations++, () => terminations++, null);
        playback.Dispose();

        Assert.Equal(0, initializations);
        Assert.Equal(0, terminations);
    }

    [Fact]
    public void Play_that_fails_before_a_stream_opens_releases_its_PortAudio_reference()
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var fileName = $"playback-test-{Guid.NewGuid():N}.wav";
        var path = Path.Join(TypeWhisperEnvironment.AudioPath, fileName);
        // Stereo is rejected after PortAudio is taken, so no device is ever touched.
        File.WriteAllBytes(path, CreatePcm16Wav(channels: 2, sampleRate: 16000, frames: 160));
        var initializations = 0;
        var terminations = 0;

        try
        {
            using var playback = new AudioPlaybackService(
                () => initializations++,
                () => terminations++,
                null
            );

            playback.Play(fileName);

            Assert.False(playback.IsPlaying);
            Assert.Equal(1, initializations);
            Assert.Equal(1, terminations);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePcm16Wav(short channels, int sampleRate, int frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var blockAlign = (short)(channels * 2);
        var dataLength = frames * blockAlign;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }
}
