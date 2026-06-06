namespace TypeWhisper.Cli.Services;

/// <summary>
///     Pure-function audio magic-byte sniffer used by the <c>transcribe -</c>
///     stdin path so the server-side filename hint matches the actual
///     container. Returns a short extension (no leading dot). Defaults to
///     "wav" when no header is recognized.
/// </summary>
internal static class StdinAudioSniffer
{
    public static string Detect(ReadOnlySpan<byte> head)
    {
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (head.Length >= 12
            && head[0] == (byte)'R' && head[1] == (byte)'I'
            && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'A'
            && head[10] == (byte)'V' && head[11] == (byte)'E')
        {
            return "wav";
        }

        if (head.Length >= 4
            && head[0] == (byte)'f' && head[1] == (byte)'L'
            && head[2] == (byte)'a' && head[3] == (byte)'C')
        {
            return "flac";
        }

        if (head.Length >= 4
            && head[0] == (byte)'O' && head[1] == (byte)'g'
            && head[2] == (byte)'g' && head[3] == (byte)'S')
        {
            return "ogg";
        }

        if (head.Length >= 3
            && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
        {
            return "mp3";
        }

        // MPEG audio frame sync (mp3 with no ID3 tag): 0xFF followed by
        // 0xFB / 0xF3 / 0xF2 (or other 0xFx variants for MPEG-2/2.5).
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            return "mp3";
        }

        return "wav";
    }
}
