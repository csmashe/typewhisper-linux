using System.Text.Json;
using TypeWhisper.Core.Services;

// The delegate asserts on the token it receives; ReSharper reads xUnit asserts as
// precondition checks and concludes the parameter is only validated, never used —
// but asserting on it is exactly the test's purpose, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
namespace TypeWhisper.Core.Tests.Services;

public sealed class SegmentTranslationBatchesTests
{
    [Theory]
    [InlineData("[{\"id\":0,\"text\":\" Hallo \"},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("```json\n[{\"id\":0,\"text\":\" Hallo \"},{\"id\":1,\"text\":\"Welt\"}]\n```")]
    [InlineData("```\r\n[{\"id\":0,\"text\":\" Hallo \"},{\"id\":1,\"text\":\"Welt\"}]\r\n```")]
    public async Task ValidReplyPreservesOrderAndTrimsText(string reply)
    {
        var result = await SegmentTranslationBatches.TranslateAsync(
            ["Hello", "World"], (_, _) => Task.FromResult(reply), CancellationToken.None);

        Assert.Equal(["Hallo", "Welt"], result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"id\":1,\"text\":\"Welt\"},{\"id\":0,\"text\":\"Hallo\"}]")]
    [InlineData("[{\"id\":0,\"text\":\"Hallo\"},{\"id\":0,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":0,\"text\":\" \"},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":0,\"text\":null},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":0,\"text\":\"Hallo\",\"extra\":true},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("{}")]
    [InlineData("[null,{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":\"0\",\"text\":\"Hallo\"},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":0.5,\"text\":\"Hallo\"},{\"id\":1,\"text\":\"Welt\"}]")]
    [InlineData("[{\"id\":0,\"id\":0},{\"id\":1,\"text\":\"Welt\"}]")]
    public async Task InvalidReplyThrowsMismatch(string reply)
    {
        var exception = await Assert.ThrowsAsync<SegmentTranslationMismatchException>(() =>
            SegmentTranslationBatches.TranslateAsync(
                ["Hello", "World"], (_, _) => Task.FromResult(reply), CancellationToken.None));

        Assert.Equal(
            "Translation did not preserve the subtitle segments. Retry with another LLM model.",
            exception.Message);
    }

    [Fact]
    public async Task SegmentLimitUsesGlobalIdsAcrossThreeBatches()
    {
        var sizes = new List<int>();
        var ids = new List<int>();
        var texts = Enumerable.Range(0, 65).Select(i => $"Text {i}").ToList();

        var result = await SegmentTranslationBatches.TranslateAsync(texts, (json, _) =>
        {
            using var document = JsonDocument.Parse(json);
            sizes.Add(document.RootElement.GetArrayLength());
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                ids.Add(entry.GetProperty("id").GetInt32());
                Assert.Equal(texts[ids[^1]], entry.GetProperty("text").GetString());
            }

            return Task.FromResult(json);
        }, CancellationToken.None);

        Assert.Equal([32, 32, 1], sizes);
        Assert.Equal(Enumerable.Range(0, 65), ids);
        Assert.Equal(64, ids[^1]);
        Assert.Equal(texts, result);
    }

    [Theory]
    [InlineData(5000, 2, 2)]
    [InlineData(9000, 1, 1)]
    [InlineData(4000, 2, 1)]
    public async Task CharacterLimitAllowsAnOverlongSegment(int length, int count, int expectedCalls)
    {
        var texts = Enumerable.Repeat(new string('a', length), count).ToList();
        var sizes = new List<int>();

        var result = await SegmentTranslationBatches.TranslateAsync(texts, (json, _) =>
        {
            using var document = JsonDocument.Parse(json);
            sizes.Add(document.RootElement.GetArrayLength());
            return Task.FromResult(json);
        }, CancellationToken.None);

        Assert.Equal(expectedCalls, sizes.Count);
        Assert.All(sizes, size => Assert.Equal(count / expectedCalls, size));
        Assert.Equal(texts, result);
    }

    [Fact]
    public async Task CancellationInsideDelegateThrowsWithoutReturningPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SegmentTranslationBatches.TranslateAsync(
                Enumerable.Repeat("text", 65).ToList(),
                (json, token) =>
                {
                    // ReSharper disable AccessToDisposedClosure -- runs synchronously while TranslateAsync is awaited, before the using disposes cancellation.
                    Assert.Equal(cancellation.Token, token);
                    if (++calls == 2)
                    {
                        cancellation.Cancel();
                    }
                    // ReSharper restore AccessToDisposedClosure

                    return Task.FromResult(json);
                },
                cancellation.Token));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BlankSegmentsAreKeptInPlaceAndNotSent()
    {
        var requests = new List<string>();
        var result = await SegmentTranslationBatches.TranslateAsync(
            ["Hello", "", "  ", "World"],
            (json, _) =>
            {
                requests.Add(json);
                return Task.FromResult("[{\"id\":0,\"text\":\"Hallo\"},{\"id\":3,\"text\":\"Welt\"}]");
            },
            CancellationToken.None);

        Assert.Equal(["Hallo", "", "  ", "Welt"], result);
        Assert.Equal(["[{\"id\":0,\"text\":\"Hello\"},{\"id\":3,\"text\":\"World\"}]"], requests);
    }

    [Fact]
    public async Task AllBlankSegmentsDoNotCallDelegate()
    {
        var calls = 0;
        var result = await SegmentTranslationBatches.TranslateAsync(["", " "], (_, _) =>
        {
            calls++;
            return Task.FromResult("[]");
        }, CancellationToken.None);

        Assert.Equal(["", " "], result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EmptyInputDoesNotCallDelegate()
    {
        var calls = 0;
        var result = await SegmentTranslationBatches.TranslateAsync([], (_, _) =>
        {
            calls++;
            return Task.FromResult("[]");
        }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, calls);
    }
}
