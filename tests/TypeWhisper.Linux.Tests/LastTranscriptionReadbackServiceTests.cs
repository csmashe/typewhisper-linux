using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK.Models;
using Xunit;
using static TypeWhisper.Linux.Tests.SpeechFeedbackServiceTests;

namespace TypeWhisper.Linux.Tests;

public sealed class LastTranscriptionReadbackServiceTests
{
    [Fact]
    public void Toggle_SpeaksLatestEntryWithRecordLanguage()
    {
        var now = DateTime.UtcNow;
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.SetupGet(service => service.Records).Returns([
            new TranscriptionRecord { Id = "old", RawText = "older", FinalText = "older", Timestamp = now.AddMinutes(-1) },
            new TranscriptionRecord { Id = "LATEST", RawText = "persisted", FinalText = "persisted", Language = "de", Timestamp = now },
            new TranscriptionRecord
            {
                Id = "failed", RawText = "failed", FinalText = "failed", Timestamp = now.AddMinutes(1),
                Status = TranscriptionRecordStatus.TranscriptionFailed,
            },
        ]);
        var store = new RecentTranscriptionStore();
        store.RecordTranscription("latest", "session text", now, null, null);
        var provider = new ControlledTtsProvider(new ControlledPlaybackSession());
        using var speech = CreateSpeech(provider);
        var sut = new LastTranscriptionReadbackService(history.Object, store, speech);

        sut.Toggle();

        var request = Assert.Single(provider.Requests);
        Assert.Equal("session text", request.Text);
        Assert.Equal("de", request.Language);
        Assert.Equal(TtsPurpose.ManualReadback, request.Purpose);
        history.VerifyGet(service => service.Records, Times.Once);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public void Toggle_WhileSpeaking_StopsWithoutQueueing()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.SetupGet(service => service.Records).Returns([
            new TranscriptionRecord
            {
                Id = "latest", RawText = "read this", FinalText = "read this",
                Language = "es", Timestamp = DateTime.UtcNow,
            },
        ]);
        var session = new ControlledPlaybackSession(completeOnStop: true);
        var provider = new ControlledTtsProvider(session);
        using var speech = CreateSpeech(provider);
        var sut = new LastTranscriptionReadbackService(history.Object, new RecentTranscriptionStore(), speech);

        sut.Toggle();
        Assert.True(session.IsActive);
        sut.Toggle();

        Assert.Equal(1, session.StopCount);
        Assert.False(session.IsActive);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public void Toggle_WithHistoryClearedWhileSpeaking_StopsInsteadOfReportingEmptyState()
    {
        var records = new List<TranscriptionRecord>
        {
            new()
            {
                Id = "latest", RawText = "read this", FinalText = "read this",
                Timestamp = DateTime.UtcNow,
            },
        };
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.SetupGet(service => service.Records).Returns(() => records);
        var session = new ControlledPlaybackSession(completeOnStop: true);
        using var speech = CreateSpeech(new ControlledTtsProvider(session));
        var sut = new LastTranscriptionReadbackService(history.Object, new RecentTranscriptionStore(), speech);
        var feedback = new List<string>();
        sut.FeedbackRequested += (message, _) => feedback.Add(message);

        sut.Toggle();
        records.Clear();
        sut.Toggle();

        Assert.Equal(1, session.StopCount);
        Assert.False(session.IsActive);
        Assert.Empty(feedback);
    }

    [Fact]
    public void Toggle_WithEmptyHistory_RaisesFeedbackAndNoSpeech()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.SetupGet(service => service.Records).Returns([]);
        var provider = new ControlledTtsProvider();
        using var speech = CreateSpeech(provider);
        var sut = new LastTranscriptionReadbackService(history.Object, new RecentTranscriptionStore(), speech);
        var feedback = new List<(string Message, bool IsError)>();
        sut.FeedbackRequested += (message, isError) => feedback.Add((message, isError));

        sut.Toggle();

        Assert.Equal((Loc.Instance["Overlay.NoRecentTranscriptions"], false), Assert.Single(feedback));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void Toggle_SessionOnlyEntry_UsesConfiguredLanguageFallback()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.SetupGet(service => service.Records).Returns([]);
        var store = new RecentTranscriptionStore();
        store.RecordTranscription("session", "read this", DateTime.UtcNow, null, null);
        var provider = new ControlledTtsProvider(new ControlledPlaybackSession());
        using var speech = CreateSpeech(provider);
        var sut = new LastTranscriptionReadbackService(history.Object, store, speech);

        sut.Toggle();

        Assert.Equal("ru", Assert.Single(provider.Requests).Language);
    }

    private static SpeechFeedbackService CreateSpeech(ControlledTtsProvider provider)
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = false, Language = "ru" }
        );
        return new SpeechFeedbackService(settings.Object, TestPluginManagerFactory.Create(), provider);
    }
}
