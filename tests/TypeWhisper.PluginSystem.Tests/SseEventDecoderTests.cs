using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SseEventDecoderTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task DecodeAsync_AcceptsAllSseLineEndings(string lineEnding)
    {
        using var reader = new StringReader($"data: value{lineEnding}{lineEnding}");

        var events = await DecodeAllAsync(reader);

        var sseEvent = Assert.Single(events);
        Assert.Equal("value", sseEvent.Data);
    }

    [Fact]
    public async Task DecodeAsync_SplitsAtFirstColonAndRemovesExactlyOneSpace()
    {
        using var reader = new StringReader("data:  value:with:colons\n\n");

        var sseEvent = Assert.Single(await DecodeAllAsync(reader));

        Assert.Equal(" value:with:colons", sseEvent.Data);
    }

    [Fact]
    public async Task DecodeAsync_UsesDefaultMessageTypeAndResetsEventAfterDispatch()
    {
        using var reader = new StringReader(
            "event: custom\ndata: first\n\ndata: second\n\n");

        var events = await DecodeAllAsync(reader);

        Assert.Equal(2, events.Count);
        Assert.Equal("custom", events[0].EventType);
        Assert.Equal("message", events[1].EventType);
    }

    [Fact]
    public async Task DecodeAsync_EventOnlyBlockDoesNotLabelTheNextEvent()
    {
        using var reader = new StringReader("event: error\n\ndata: payload\n\n");

        var sseEvent = Assert.Single(await DecodeAllAsync(reader));

        Assert.Equal("payload", sseEvent.Data);
        Assert.Equal("message", sseEvent.EventType);
    }

    [Fact]
    public async Task DecodeAsync_PersistsResetsAndRejectsInvalidLastEventIds()
    {
        using var reader = new StringReader(
            "id: one\ndata: first\n\n"
            + "data: second\n\n"
            + "id: ignored\0value\ndata: third\n\n"
            + "id:\ndata: fourth\n\n"
            + "data: fifth\n\n");

        var events = await DecodeAllAsync(reader);

        Assert.Equal(["one", "one", "one", "", ""],
            events.Select(sseEvent => sseEvent.LastEventId));
    }

    [Fact]
    public async Task DecodeAsync_CommentOnlyUnknownAndRetryBlocksDispatchNothing()
    {
        using var reader = new StringReader(
            ": ping\nunknown: value\nretry: 1000\n\n"
            + "data: actual\n\n");

        var sseEvent = Assert.Single(await DecodeAllAsync(reader));

        Assert.Equal("actual", sseEvent.Data);
    }

    [Fact]
    public async Task DecodeAsync_LeadingWhitespaceBeforeFieldNameIsNotTrimmed()
    {
        using var reader = new StringReader(" data: ignored\n\ndata: accepted\n\n");

        var sseEvent = Assert.Single(await DecodeAllAsync(reader));

        Assert.Equal("accepted", sseEvent.Data);
    }

    [Fact]
    public async Task DecodeAsync_JoinsMultipleDataFieldsWithLineFeeds()
    {
        using var reader = new StringReader("data: first\ndata: second\n\n");

        var sseEvent = Assert.Single(await DecodeAllAsync(reader));

        Assert.Equal("first\nsecond", sseEvent.Data);
    }

    [Fact]
    public async Task DecodeAsync_DiscardsPendingDataAtEof()
    {
        using var reader = new StringReader("data: not-dispatched\n");

        Assert.Empty(await DecodeAllAsync(reader));
    }

    private static async Task<List<SseEvent>> DecodeAllAsync(TextReader reader)
    {
        var events = new List<SseEvent>();
        await foreach (var sseEvent in SseEventDecoder.DecodeAsync(reader))
            events.Add(sseEvent);
        return events;
    }
}
