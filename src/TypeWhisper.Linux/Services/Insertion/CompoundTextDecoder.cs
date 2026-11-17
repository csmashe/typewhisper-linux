using System.Text;

namespace TypeWhisper.Linux.Services.Insertion;

/// <summary>Decodes X11 COMPOUND_TEXT (ISO 2022 with the X Consortium's designations).</summary>
internal static class CompoundTextDecoder
{
    private const byte Esc = 0x1B;
    private const byte Csi = 0x9B;
    private const byte EucSingleShift2 = 0x8E;

    private static readonly Charset s_ascii = new(Encoding.Latin1, 1, false);
    private static readonly Charset s_latin1Upper = new(Encoding.Latin1, 1, true) { Is96 = true };

    // JIS X0201 Roman is ASCII except for these two positions.
    private static readonly Charset s_jisRoman = new(Encoding.Latin1, 1, false)
    {
        Fixups = new Dictionary<char, char> { ['\\'] = '\u00A5', ['~'] = '\u203E' },
    };

    // Code page 51932 follows CP932 for these JIS X0208 cells; libX11 uses the JIS X0208 code points.
    private static readonly Dictionary<char, char> s_jisX0208Fixups = new()
    {
        ['\uFF5E'] = '\u301C',
        ['\u2225'] = '\u2016',
        ['\uFF0D'] = '\u2212',
        ['\uFFE0'] = '\u00A2',
        ['\uFFE1'] = '\u00A3',
        ['\uFFE2'] = '\u00AC',
    };

    // Code page 936 follows GBK for these GB2312 cells; libX11 uses the GB2312 code points.
    private static readonly Dictionary<char, char> s_gb2312Fixups = new()
    {
        ['\u00B7'] = '\u30FB',
        ['\u2014'] = '\u2015',
    };

    // Code page 51949 decodes KS C 5601 cell 0x3453 to U+B2D6; libX11 uses U+B2D2.
    private static readonly Dictionary<char, char> s_ksc5601Fixups = new() { ['\uB2D6'] = '\uB2D2' };

    /// <summary>Null when the bytes are malformed or use a charset this decoder does not know.</summary>
    public static string? TryDecode(ReadOnlySpan<byte> data)
    {
        try
        {
            return Decode(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? Decode(ReadOnlySpan<byte> data)
    {
        var gl = s_ascii;
        var gr = s_latin1Upper;
        var output = new StringBuilder(data.Length);
        var run = new List<byte>();
        Charset? runCharset = null;

        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            switch (b)
            {
                case Esc:
                {
                    if (TryReadUtf8Segment(data, i, out var utf8, out var next))
                    {
                        Flush();
                        output.Append(utf8);
                        i = next;
                        continue;
                    }

                    if (!TryDesignate(data, ref i, ref gl, ref gr))
                    {
                        return null;
                    }

                    continue;
                }
                case Csi:
                    // Direction markers (CSI 1 ], CSI 2 ], CSI ]) carry no characters.
                    i++;
                    while (i < data.Length && data[i] is >= 0x30 and <= 0x3F)
                    {
                        i++;
                    }

                    if (i >= data.Length || data[i] != (byte)']')
                    {
                        return null;
                    }

                    i++;
                    continue;
                case 0x09 or 0x0A or 0x20:
                    Append(s_ascii, [b]);
                    i++;
                    continue;
                case < 0x20 or 0x7F or >= 0x80 and < 0xA0:
                    return null;
            }

            var charset = b < 0x80 ? gl : gr;
            if (charset is { Is96: false } && b is 0xA0 or 0xFF)
            {
                return null;
            }

            if (charset.Width == 2)
            {
                if (i + 1 >= data.Length || (data[i + 1] & 0x80) != (b & 0x80))
                {
                    return null;
                }

                Append(charset, [(byte)(b | 0x80), (byte)(data[i + 1] | 0x80)]);
                i += 2;
                continue;
            }

            var mapped = charset.HighHalf ? (byte)(b | 0x80) : (byte)(b & 0x7F);
            if (charset.SingleShift is { } prefix)
            {
                Append(charset, [prefix, mapped]);
            }
            else
            {
                Append(charset, [mapped]);
            }

            i++;
        }

        Flush();
        return output.ToString();

        void Append(Charset charset, ReadOnlySpan<byte> bytes)
        {
            if (!ReferenceEquals(charset, runCharset))
            {
                Flush();
                runCharset = charset;
            }

            foreach (var b in bytes)
            {
                run.Add(b);
            }
        }

        void Flush()
        {
            if (runCharset is not null && run.Count > 0)
            {
                var text = runCharset.Encoding.GetString(run.ToArray());
                if (runCharset.Fixups is { } fixups)
                {
                    foreach (var c in text)
                    {
                        output.Append(fixups.GetValueOrDefault(c, c));
                    }
                }
                else
                {
                    output.Append(text);
                }
            }

            run.Clear();
        }
    }

    // ESC % G opens a UTF-8 segment that runs to ESC % @ (or the end of the data).
    private static bool TryReadUtf8Segment(ReadOnlySpan<byte> data, int start, out string text, out int next)
    {
        text = string.Empty;
        next = start;
        if (start + 2 >= data.Length || data[start + 1] != (byte)'%' || data[start + 2] != (byte)'G')
        {
            return false;
        }

        var body = data[(start + 3)..];
        var end = body.IndexOf([Esc, (byte)'%', (byte)'@']);
        var segment = end < 0 ? body : body[..end];
        text = new UTF8Encoding(false, true).GetString(segment);
        next = start + 3 + segment.Length + (end < 0 ? 0 : 3);
        return true;
    }

    private static bool TryDesignate(ReadOnlySpan<byte> data, ref int i, ref Charset gl, ref Charset gr)
    {
        var start = i + 1;
        var end = start;
        while (end < data.Length && data[end] is >= 0x20 and <= 0x2F)
        {
            end++;
        }

        if (end >= data.Length || data[end] is < 0x30 or > 0x7E)
        {
            return false;
        }

        var intermediates = Encoding.ASCII.GetString(data[start..end]);
        var final = (char)data[end];
        i = end + 1;

        switch (intermediates)
        {
            case "%" when final == '@':
                return true;
            case "(" when Single94(final) is { } set:
                gl = set;
                return true;
            case ")" when Single94(final) is { } set:
                gr = set;
                return true;
            case "-" when Single96(final) is { } set:
                gr = set;
                return true;
            case "$(" or "$" when Double94(final) is { } set:
                gl = set;
                return true;
            case "$)" when Double94(final) is { } set:
                gr = set;
                return true;
            default:
                return false;
        }
    }

    private static Charset? Single94(char final) => final switch
    {
        'B' => s_ascii,
        'J' => s_jisRoman,
        'I' when CodePage(51932) is { } eucJp => new Charset(eucJp, 1, true, EucSingleShift2),
        _ => null,
    };

    private static Charset? Single96(char final)
    {
        int? codePage = final switch
        {
            'A' => null,
            'B' => 28592,
            'C' => 28593,
            'D' => 28594,
            'F' => 28597,
            'G' => 28596,
            'H' => 28598,
            'L' => 28595,
            'M' => 28599,
            'Y' => 28603,
            'b' => 28605,
            _ => -1,
        };
        if (codePage is null)
        {
            return s_latin1Upper;
        }

        return CodePage(codePage.Value) is { } encoding ? new Charset(encoding, 1, true) { Is96 = true } : null;
    }

    private static Charset? Double94(char final)
    {
        var codePage = final switch
        {
            'A' => 936,
            'B' => 51932,
            'C' => 51949,
            _ => -1,
        };
        var fixups = final switch
        {
            'A' => s_gb2312Fixups,
            'B' => s_jisX0208Fixups,
            'C' => s_ksc5601Fixups,
            _ => null,
        };
        return CodePage(codePage) is { } encoding ? new Charset(encoding, 2, true) { Fixups = fixups } : null;
    }

    private static Encoding? CodePage(int codePage) =>
        codePage < 0
            ? null
            : CodePagesEncodingProvider.Instance.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            );

    private sealed record Charset(Encoding Encoding, int Width, bool HighHalf, byte? SingleShift = null)
    {
        public bool Is96 { get; init; }

        public IReadOnlyDictionary<char, char>? Fixups { get; init; }
    }
}
