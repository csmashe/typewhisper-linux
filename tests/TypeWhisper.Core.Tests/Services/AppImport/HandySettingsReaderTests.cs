using System.Text;
using TypeWhisper.Core.Services.AppImport;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services.AppImport;

/// <summary>Covers <see cref="HandySettingsReader" />: Handy's custom-word shape, torn reads, limits and cancellation.</summary>
public sealed class HandySettingsReaderTests
{
    private const string HandySettings =
        """{"settings":{"custom_words":[" München ","TypeWhisper"],"custom_filler_words":["remove me"],"selected_language":"auto"},"unrelated":"ignored"}""";

    private static Func<string, CancellationToken, byte[]> Reads(params string[] contents)
    {
        var queue = new Queue<string>(contents);
        return (_, _) => Encoding.UTF8.GetBytes(queue.Dequeue());
    }

    [Fact]
    public void ReadsOnlyTheTrimmedCustomWords()
    {
        var words = HandySettingsReader.ReadCustomWords("x", CancellationToken.None, Reads(HandySettings, HandySettings));

        Assert.Equal(["München", "TypeWhisper"], words);
    }

    [Fact]
    public void RetriesWhenConsecutiveReadsDiffer()
    {
        var reads = 0;
        var contents = new[] { "{}", HandySettings, HandySettings };
        var words = HandySettingsReader.ReadCustomWords(
            "x",
            CancellationToken.None,
            (_, _) => Encoding.UTF8.GetBytes(contents[Math.Min(reads++, contents.Length - 1)])
        );

        Assert.Equal(["München", "TypeWhisper"], words);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void RetriesATornFileThatWasReadIdenticallyTwice()
    {
        const string torn = """{"settings":{"custom_wo""";

        var words = HandySettingsReader.ReadCustomWords(
            "x",
            CancellationToken.None,
            Reads(torn, torn, HandySettings, HandySettings)
        );

        Assert.Equal(["München", "TypeWhisper"], words);
    }

    [Fact]
    public void AContinuouslyChangingFileIsRejectedAsUnstable()
    {
        var reads = 0;
        var ex = Assert.Throws<AppImportReadException>(() =>
            HandySettingsReader.ReadCustomWords(
                "x",
                CancellationToken.None,
                (_, _) => Encoding.UTF8.GetBytes("{\"settings\":{\"custom_words\":[\"" + reads++ + "\"]}}")
            )
        );

        Assert.Equal(AppImportReadFailure.Unstable, ex.Failure);
        Assert.Equal(6, reads);
    }

    [Fact]
    public void AFileThatStaysMalformedIsRejectedAsMalformedAfterRetrying()
    {
        var ex = Assert.Throws<AppImportReadException>(() =>
            HandySettingsReader.ReadCustomWords("x", CancellationToken.None, (_, _) => "[1]"u8.ToArray())
        );

        Assert.Equal(AppImportReadFailure.Malformed, ex.Failure);
    }

    [Fact]
    public void CancellationStopsBeforeTheSecondRead()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HandySettingsReader.ReadCustomWords(
                "x",
                cancellation.Token,
                (_, _) =>
                {
                    reads++;
                    // ReSharper disable once AccessToDisposedClosure -- the read runs synchronously inside ReadCustomWords, before the using disposes cancellation.
                    cancellation.Cancel();
                    return Encoding.UTF8.GetBytes(HandySettings);
                }
            )
        );
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"settings":null}""")]
    [InlineData("""{"settings":{}}""")]
    [InlineData("""{"settings":{"custom_words":null}}""")]
    [InlineData("""{"settings":{"custom_words":[]}}""")]
    public void MissingOrNullWordListIsEmpty(string json)
    {
        Assert.Empty(HandySettingsReader.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1]")]
    [InlineData("""{"settings":[]}""")]
    [InlineData("""{"settings":{"custom_words":"word"}}""")]
    [InlineData("""{"settings":{"custom_words":[1]}}""")]
    [InlineData("""{"settings":{"custom_words":["a"],"custom_words":["b"]}}""")]
    [InlineData("""{"settings":{},"settings":{"custom_words":["a"]}}""")]
    [InlineData("""{"settings":{"custom_words":["a"]}} garbage""")]
    [InlineData("""{"settings":{"custom_words":["a"]}}{"settings":{}}""")]
    public void MalformedSettingsAreRejected(string json)
    {
        var ex = Assert.Throws<AppImportReadException>(() => HandySettingsReader.Decode(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(AppImportReadFailure.Malformed, ex.Failure);
    }

    [Fact]
    public void AByteOrderMarkIsAccepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(HandySettings)).ToArray();

        Assert.Equal(["München", "TypeWhisper"], HandySettingsReader.Decode(bytes));
    }

    [Fact]
    public void MoreThanTheWordLimitIsRejected()
    {
        var words = string.Join(",", Enumerable.Range(0, HandySettingsReader.MaxWords + 1).Select(i => $"\"w{i}\""));

        var ex = Assert.Throws<AppImportReadException>(() =>
            HandySettingsReader.Decode(Encoding.UTF8.GetBytes("{\"settings\":{\"custom_words\":[" + words + "]}}"))
        );

        Assert.Equal(AppImportReadFailure.TooManyEntries, ex.Failure);
    }

    [Fact]
    public void ReadsARealFileAndRejectsOneOverTheSizeLimit()
    {
        var directory = TestPaths.CreateTempDirectory("handy-settings");
        try
        {
            var path = Path.Join(directory, HandySettingsReader.SettingsFileName);
            File.WriteAllText(path, HandySettings);
            Assert.Equal(["München", "TypeWhisper"], HandySettingsReader.ReadCustomWords(path, CancellationToken.None));

            File.WriteAllBytes(path, new byte[HandySettingsReader.MaxFileBytes + 1]);
            var ex = Assert.Throws<AppImportReadException>(() =>
                HandySettingsReader.ReadCustomWords(path, CancellationToken.None)
            );
            Assert.Equal(AppImportReadFailure.TooLarge, ex.Failure);

            Assert.Throws<FileNotFoundException>(() =>
                HandySettingsReader.ReadCustomWords(Path.Join(directory, "missing.json"), CancellationToken.None)
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
