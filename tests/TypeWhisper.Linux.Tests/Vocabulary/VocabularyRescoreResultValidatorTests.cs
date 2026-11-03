using TypeWhisper.Linux.Services.Vocabulary;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests.Vocabulary;

public sealed class VocabularyRescoreResultValidatorTests
{
    private static VocabularyRescoreRequest Request(string text = "one two three") =>
        new(
            Guid.NewGuid(),
            text,
            ReadOnlyMemory<float>.Empty,
            16000,
            [],
            [new VocabularyTermHint("TypeWhisper"), new VocabularyTermHint("CTC")]
        );

    [Fact]
    public void NonOverlappingReplacements_AppliedFromEnd()
    {
        var request = Request();
        Assert.Equal(
            "TypeWhisper two CTC",
            VocabularyRescoreResultValidator.Apply(
                request,
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [
                        new VocabularyReplacement(8, 5, "CTC", 0.5),
                        new VocabularyReplacement(0, 3, "TypeWhisper", 1),
                    ]
                )
            )
        );
    }

    [Fact]
    public void WrongRecordingId_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            VocabularyRescoreResultValidator.Apply(
                Request(),
                new VocabularyRescoreResult(Guid.NewGuid(), [])
            )
        );

    [Fact]
    public void OverlappingSpans_Throw()
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() =>
            VocabularyRescoreResultValidator.Apply(
                request,
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [
                        new VocabularyReplacement(0, 3, "CTC", 1),
                        new VocabularyReplacement(2, 3, "CTC", 1),
                    ]
                )
            )
        );
    }

    [Theory]
    [InlineData(-1, 1, "CTC", 1)]
    [InlineData(12, 2, "CTC", 1)]
    [InlineData(0, 0, "CTC", 1)]
    [InlineData(0, 1, "unknown", 1)]
    [InlineData(0, 1, "CTC", double.NaN)]
    [InlineData(0, 1, "CTC", double.PositiveInfinity)]
    [InlineData(0, 1, "CTC", double.NegativeInfinity)]
    public void InvalidReplacement_Throws(int start, int length, string term, double score)
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() =>
            VocabularyRescoreResultValidator.Apply(
                request,
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(start, length, term, score)]
                )
            )
        );
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void InsideCombiningSequence_Throws(int start, int length)
    {
        var request = Request("e\u0301");
        Assert.Throws<InvalidDataException>(() =>
            VocabularyRescoreResultValidator.Apply(
                request,
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(start, length, "CTC", 1)]
                )
            )
        );
    }

    [Fact]
    public void EmptyReplacements_ReturnOriginal()
    {
        var request = Request();
        Assert.Equal(
            request.Text,
            VocabularyRescoreResultValidator.Apply(
                request,
                new VocabularyRescoreResult(request.RecordingId, [])
            )
        );
    }
}
