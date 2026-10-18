using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.CloudflareAsr;

/// <summary>
///     Whisper Large V3 Turbo request body: <c>{"audio":"&lt;base64 WAV&gt;", "task":…}</c>, with the
///     audio encoded straight into the upload stream instead of building a second copy as a string.
/// </summary>
// ReSharper disable once UnusedType.Global -- constructed by CloudflareAsrPlugin.TranscribeAsync for the Turbo model.
internal sealed class TurboAudioContent : HttpContent
{
    private static readonly byte[] s_prefix = "{\"audio\":\""u8.ToArray();
    private readonly byte[] _audio;
    private readonly byte[] _suffix;

    internal TurboAudioContent(byte[] audio, string? language, string? initialPrompt)
    {
        _audio = audio;
        var fields = new Dictionary<string, string> { ["task"] = "transcribe" };
        if (language is not null)
            fields["language"] = language;
        if (initialPrompt is not null)
            fields["initial_prompt"] = initialPrompt;
        // Closes the audio string, then reuses the serialized object without its opening brace.
        _suffix = Encoding.UTF8.GetBytes("\"," + JsonSerializer.Serialize(fields)[1..]);
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override bool TryComputeLength(out long length)
    {
        length = s_prefix.Length + (_audio.LongLength + 2) / 3 * 4 + _suffix.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken
    )
    {
        // A multiple of three, so no chunk but the last one is padded.
        const int inputChunkSize = 12_288;
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            await stream.WriteAsync(s_prefix, cancellationToken);
            for (var offset = 0; offset < _audio.Length; offset += inputChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(inputChunkSize, _audio.Length - offset);
                Base64.EncodeToUtf8(_audio.AsSpan(offset, count), buffer, out _, out var written);
                await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            }

            await stream.WriteAsync(_suffix, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
