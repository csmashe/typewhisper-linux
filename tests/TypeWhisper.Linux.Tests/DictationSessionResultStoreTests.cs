using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class DictationSessionResultStoreTests
{
    private static DictationSessionResult Sample(int id) =>
        new(id, "ready", "hello", "hello raw", "en", 1.23, "whisper", "tiny");

    private static DictationSessionResult Failed(int id) =>
        new(id, "failed", string.Empty, null, null, 0, null, null, "boom");

    [Fact]
    public void Record_ThenTryGet_ReturnsResult()
    {
        var store = new DictationSessionResultStore();
        store.Record(Sample(7));

        Assert.True(store.TryGet(7, out var stored));
        Assert.Equal(7, stored.SessionId);
        Assert.Equal("hello", stored.Text);
        Assert.Equal("en", stored.Language);
    }

    [Fact]
    public void TryGet_UnknownSession_ReturnsFalse()
    {
        var store = new DictationSessionResultStore();
        Assert.False(store.TryGet(42, out _));
    }

    [Fact]
    public void TryGet_AfterTtl_ReturnsFalse()
    {
        var store = new DictationSessionResultStore(TimeSpan.FromMilliseconds(50));
        store.Record(Sample(1));
        Thread.Sleep(150);
        store.EvictNow(DateTime.UtcNow);

        Assert.False(store.TryGet(1, out _));
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        var store = new DictationSessionResultStore();
        store.Record(Sample(1));
        store.Clear(1);

        Assert.False(store.TryGet(1, out _));
    }

    [Fact]
    public void Record_FailedStatus_RoundTrips()
    {
        var store = new DictationSessionResultStore();
        store.Record(Failed(5));

        Assert.True(store.TryGet(5, out var stored));
        Assert.Equal("failed", stored.Status);
        Assert.Equal("boom", stored.Message);
    }

    [Fact]
    public void Record_Concurrent_AddsAllSessions()
    {
        var store = new DictationSessionResultStore();

        Parallel.For(0, 200, i => store.Record(Sample(i + 1)));

        for (var i = 1; i <= 200; i++)
        {
            Assert.True(store.TryGet(i, out _));
        }
    }
}
