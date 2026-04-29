using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IHistoryInsightsService
{
    HistoryInsights Build(IReadOnlyList<TranscriptionRecord> records, int topAppCount = 5);
}
