using TypeWhisper.Linux.Services.Insertion;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CompoundTextDecoderTests
{
    [Fact]
    public void Decodes_gtk_compound_text_with_latin1_greek_and_jis_segments()
    {
        // Bytes a GTK3 owner served for "PRIOR café Ω 東京" through gtk_selection_data_set_text.
        byte[] data =
        [
            .. "PRIOR caf"u8, 0xE9, (byte)' ',
            0x1B, (byte)'-', (byte)'F', 0xD9, (byte)' ',
            0x1B, (byte)'$', (byte)'(', (byte)'B', .. "El5~"u8,
        ];

        Assert.Equal("PRIOR café Ω 東京", CompoundTextDecoder.TryDecode(data));
    }

    [Fact]
    public void Decodes_double_byte_set_in_right_half_and_utf8_segment()
    {
        byte[] data =
        [
            0x1B, (byte)'$', (byte)')', (byte)'B', 0xC5, 0xEC, 0xB5, 0xFE, (byte)'\n',
            0x1B, (byte)'%', (byte)'G', .. "ŵ🙂"u8, 0x1B, (byte)'%', (byte)'@',
            .. "ok"u8,
        ];

        Assert.Equal("東京\nŵ🙂ok", CompoundTextDecoder.TryDecode(data));
    }

    [Theory]
    [InlineData(new byte[] { 0x1B, (byte)'$', (byte)'(', (byte)'B', (byte)'E' })]
    [InlineData(new byte[] { 0x1B, (byte)'%', (byte)'/', (byte)'1' })]
    [InlineData(new byte[] { (byte)'a', 0x07 })]
    public void Returns_null_for_malformed_or_unsupported_data(byte[] data)
    {
        Assert.Null(CompoundTextDecoder.TryDecode(data));
    }
}
