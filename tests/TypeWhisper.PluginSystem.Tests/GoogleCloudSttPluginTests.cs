using System.Buffers.Binary;
using System.Net;
using Moq;
using Moq.Protected;
using TypeWhisper.Plugin.GoogleCloudStt;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public class GoogleCloudSttPluginTests
{
    [Fact]
    public async Task TranscribeAsync_ThrowsForAudioLongerThanSynchronousLimit()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.LoadSecretAsync("api-key")).ReturnsAsync("dummy-key");

        using var sut = new GoogleCloudSttPlugin();
        await sut.ActivateAsync(host.Object);

        var wavAudio = new byte[44 + 61 * 32000];

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.TranscribeAsync(wavAudio, null, false, null, CancellationToken.None)
        );

        Assert.Equal(
            "Google Cloud STT (synchronous API) supports at most 60 seconds of audio; "
                + "this recording is 61 seconds. Use a different engine for long recordings.",
            exception.Message
        );
    }

    // A real 60s ffmpeg import has an extra LIST chunk (78-byte header, not 44);
    // duration must come from the data chunk or this boundary file rounds past 60.
    [Fact]
    public async Task TranscribeAsync_AcceptsExactSixtySecondsWithExtendedHeader()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.LoadSecretAsync("api-key")).ReturnsAsync("dummy-key");

        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[]}"),
                }
            );

        using var sut = new GoogleCloudSttPlugin(handler.Object);
        await sut.ActivateAsync(host.Object);

        var wavAudio = BuildFfmpegStyleWav(60 * 32000);

        var result = await sut.TranscribeAsync(wavAudio, null, false, null, CancellationToken.None);

        Assert.NotNull(result);
        handler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    // Mirrors ffmpeg's `-f wav pipe:1` output: RIFF/WAVE + fmt + LIST(INFO) + data,
    // with 0xffffffff placeholder sizes (a pipe can't be seeked to backfill them).
    private static byte[] BuildFfmpegStyleWav(int dataBytes)
    {
        var listBody = "INFOISFT\u000e\0\0\0Lavf62.12.102\0"u8.ToArray();
        var buffer = new byte[12 + 24 + 8 + listBody.Length + 8 + dataBytes];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0xFFFFFFFF);
        "WAVE"u8.CopyTo(span[8..]);

        var offset = 12;
        "fmt "u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 8)..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 10)..], 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12)..], 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 16)..], 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 20)..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 22)..], 16);
        offset += 24;

        "LIST"u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], (uint)listBody.Length);
        listBody.CopyTo(span[(offset + 8)..]);
        offset += 8 + listBody.Length;

        "data"u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 0xFFFFFFFF);

        return buffer;
    }
}
