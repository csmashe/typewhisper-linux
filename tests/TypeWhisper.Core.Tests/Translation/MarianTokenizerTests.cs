using TypeWhisper.Core.Translation;

namespace TypeWhisper.Core.Tests.Translation;

public sealed class MarianTokenizerTests : IDisposable
{
    private readonly string _tokenizerPath;
    private readonly MarianTokenizer _tokenizer;

    public MarianTokenizerTests()
    {
        _tokenizerPath = Path.GetTempFileName();
        File.WriteAllText(
            _tokenizerPath,
            """
            {
              "model": {
                "unk_id": 0,
                "vocab": [
                  ["<unk>", 0.0],
                  ["▁hello", -1.0],
                  ["▁world", -1.5],
                  ["world", -1.2]
                ]
              }
            }
            """
        );
        _tokenizer = MarianTokenizer.Load(_tokenizerPath, eosTokenId: 99);
    }

    public void Dispose()
    {
        if (File.Exists(_tokenizerPath))
        {
            File.Delete(_tokenizerPath);
        }
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("hello\nworld")]
    [InlineData("hello\tworld")]
    [InlineData("hello\r\nworld")]
    [InlineData("hello   world")]
    public void Encode_WhitespaceSeparatedWords_ReturnsWordInitialTokens(string input)
    {
        var result = _tokenizer.Encode(input);

        Assert.Equal([1, 2, 99], result);
    }

    [Theory]
    [InlineData("hello\nworld")]
    [InlineData("hello\tworld")]
    [InlineData("hello\r\nworld")]
    public void Encode_NonSpaceWhitespace_DoesNotEmitUnknownToken(string input)
    {
        var result = _tokenizer.Encode(input);

        Assert.DoesNotContain(0, result);
    }
}
