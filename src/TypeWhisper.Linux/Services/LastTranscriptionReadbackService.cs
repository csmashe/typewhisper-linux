using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

public sealed class LastTranscriptionReadbackService(
    IHistoryService history,
    RecentTranscriptionStore store,
    SpeechFeedbackService speech
)
{
    public event Action<string, bool>? FeedbackRequested;

    public void Toggle()
    {
        // Stop before looking for an entry, as upstream does: clearing history while
        // a readback plays must not strand speech the shortcut can no longer stop.
        if (speech.StopReadBack())
        {
            return;
        }

        var records = history.Records;
        var entry = store.LatestEntry(records);
        if (entry is null)
        {
            FeedbackRequested?.Invoke(Loc.Instance["Overlay.NoRecentTranscriptions"], false);
            return;
        }

        var language = records.FirstOrDefault(record =>
            string.Equals(record.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
        )?.Language;
        speech.ReadBack(entry.FinalText, language);
    }
}
