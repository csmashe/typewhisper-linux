using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class HistoryInsightsServiceTests
{
    private readonly HistoryInsightsService _sut = new();

    [Fact]
    public void Build_ReturnsEmptyInsightsForNoRecords()
    {
        var result = _sut.Build([]);

        Assert.Equal(0, result.TotalRecords);
        Assert.Equal(0, result.TotalWords);
        Assert.Empty(result.TopApps);
    }

    [Fact]
    public void Build_ComputesAveragesAndTopApps()
    {
        var records = new[]
        {
            Record("one two three", "code", duration: 3, TextInsertionStatus.Pasted),
            Record("one two", "code", duration: 5, TextInsertionStatus.Typed, snippetApplied: true),
            Record("one two three four", "browser", duration: 4, TextInsertionStatus.MissingPasteTool, dictionaryApplied: true),
            Record("one two three", "notes", duration: 6, TextInsertionStatus.CopiedToClipboard, cleanupApplied: true, promptApplied: true, translationApplied: true)
        };

        var result = _sut.Build(records);

        Assert.Equal(4, result.TotalRecords);
        Assert.Equal(12, result.TotalWords);
        Assert.Equal(3.0, result.AverageWordsPerDictation);
        Assert.Equal(4.5, result.AverageDurationSeconds);
        Assert.Equal("code", result.TopApps[0].AppProcessName);
        Assert.Equal(2, result.TopApps[0].RecordCount);
        Assert.Equal(5, result.TopApps[0].WordCount);
        Assert.Equal(1, result.PastedCount);
        Assert.Equal(1, result.TypedCount);
        Assert.Equal(1, result.CopiedToClipboardCount);
        Assert.Equal(1, result.FailedInsertionCount);
        Assert.Equal(2, result.SuccessfulInsertionCount);
        Assert.Equal(4, result.InsertionAttemptCount);
        Assert.Equal(50.0, result.InsertionSuccessRate);
        Assert.Equal(1, result.CleanupAppliedCount);
        Assert.Equal(1, result.SnippetAppliedCount);
        Assert.Equal(1, result.DictionaryCorrectionAppliedCount);
        Assert.Equal(1, result.PromptActionAppliedCount);
        Assert.Equal(1, result.TranslationAppliedCount);
    }

    private static TranscriptionRecord Record(
        string finalText,
        string app,
        double duration,
        TextInsertionStatus insertionStatus = TextInsertionStatus.Unknown,
        bool cleanupApplied = false,
        bool snippetApplied = false,
        bool dictionaryApplied = false,
        bool promptApplied = false,
        bool translationApplied = false) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            RawText = finalText,
            FinalText = finalText,
            AppProcessName = app,
            DurationSeconds = duration,
            InsertionStatus = insertionStatus,
            CleanupApplied = cleanupApplied,
            SnippetApplied = snippetApplied,
            DictionaryCorrectionApplied = dictionaryApplied,
            PromptActionApplied = promptApplied,
            TranslationApplied = translationApplied
        };
}
