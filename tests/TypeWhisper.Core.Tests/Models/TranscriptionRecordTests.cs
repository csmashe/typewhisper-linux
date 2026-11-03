using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public sealed class TranscriptionRecordTests
{
    [Fact]
    public void Preview_ShortText_ReturnsTextUnchanged()
    {
        var finalText = new string('a', 97);
        var record = CreateRecord(finalText);

        Assert.Same(finalText, record.Preview);
    }

    [Fact]
    public void Preview_ExactlyOneHundredUtf16Units_ReturnsTextUnchanged()
    {
        var finalText = new string('a', 100);
        var record = CreateRecord(finalText);

        Assert.Same(finalText, record.Preview);
    }

    [Fact]
    public void Preview_SurrogatePairStraddlesBoundary_ExcludesWholePair()
    {
        var prefix = new string('a', 99);
        var record = CreateRecord(prefix + "😀tail");

        var preview = record.Preview;

        Assert.Equal(prefix + "...", preview);
        AssertValidUtf16(preview);
    }

    [Fact]
    public void Preview_CombiningMarkGraphemeStraddlesBoundary_ExcludesWholeGrapheme()
    {
        var prefix = new string('a', 99);
        var record = CreateRecord(prefix + "e\u0301tail");

        var preview = record.Preview;

        Assert.Equal(prefix + "...", preview);
        AssertValidUtf16(preview);
    }

    [Fact]
    public void Preview_ZwjEmojiSequenceStraddlesBoundary_ExcludesWholeSequence()
    {
        var prefix = new string('a', 99);
        var record = CreateRecord(prefix + "👩‍🚀tail");

        var preview = record.Preview;

        Assert.Equal(prefix + "...", preview);
        AssertValidUtf16(preview);
    }

    [Fact]
    public void Preview_LongAsciiText_TruncatesAtOneHundredCharacters()
    {
        var record = CreateRecord(new string('a', 120));

        var preview = record.Preview;

        Assert.Equal(new string('a', 100) + "...", preview);
        AssertValidUtf16(preview);
    }

    [Fact]
    public void Preview_SingleGraphemeLongerThanLimit_ReturnsEllipsisOnly()
    {
        var record = CreateRecord("a" + new string('\u0301', 50_000));

        var preview = record.Preview;

        Assert.Equal("...", preview);
        AssertValidUtf16(preview);
    }

    [Fact]
    public void WordCount_CountsWhitespaceSeparatedWords()
    {
        var record = CreateRecord("one two\tthree\nfour");

        Assert.Equal(4, record.WordCount);
    }

    private static TranscriptionRecord CreateRecord(string finalText) =>
        new()
        {
            Id = "test-id",
            Timestamp = DateTime.UnixEpoch,
            RawText = finalText,
            FinalText = finalText,
        };

    private static void AssertValidUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(index + 1 < text.Length);
                Assert.True(char.IsLowSurrogate(text[index + 1]));
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[index]));
            }
        }
    }
}
