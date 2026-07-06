namespace TypeWhisper.Core.Models;

/// <summary>How long transcription history is kept: for a fixed duration, forever, or only until the app closes.</summary>
public enum HistoryRetentionMode
{
    Duration,
    Forever,
    UntilAppCloses
}
