using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecentTranscriptionStoreTests
{
    [Fact]
    public void MergedEntries_prefers_session_entry_over_history_duplicate()
    {
        var store = new RecentTranscriptionStore();
        var timestamp = DateTime.UtcNow;
        store.RecordTranscription("same", "session text", timestamp, "Editor", "code");
        var history = new[]
        {
            new TranscriptionRecord
            {
                Id = "same",
                Timestamp = timestamp.AddSeconds(-1),
                RawText = "raw",
                FinalText = "history text"
            }
        };

        var entries = store.MergedEntries(history, limit: 10);

        Assert.Single(entries);
        Assert.Equal("session text", entries[0].FinalText);
        Assert.Equal(RecentTranscriptionSource.Session, entries[0].Source);
    }

    [Fact]
    public void MergedEntries_applies_limit()
    {
        var store = new RecentTranscriptionStore();
        for (var i = 0; i < 5; i++)
        {
            store.RecordTranscription(i.ToString(), $"text {i}", DateTime.UtcNow.AddSeconds(i), null, null);
        }

        var entries = store.MergedEntries([], limit: 3);

        Assert.Equal(3, entries.Count);
        Assert.Equal("text 4", entries[0].FinalText);
    }

    [Fact]
    public void PaletteViewModel_filters_text_and_subtitle()
    {
        var entries = new[]
        {
            new RecentTranscriptionEntry("1", "alpha note", DateTime.UtcNow, "Editor", "code", RecentTranscriptionSource.Session),
            new RecentTranscriptionEntry("2", "beta note", DateTime.UtcNow, "Browser", "firefox", RecentTranscriptionSource.Session)
        };
        var sut = new RecentTranscriptionsPaletteViewModel(entries, _ => { });

        sut.SearchQuery = "firefox";

        Assert.Single(sut.FilteredEntries);
        Assert.Equal("beta note", sut.FilteredEntries[0].FinalText);
    }
}
