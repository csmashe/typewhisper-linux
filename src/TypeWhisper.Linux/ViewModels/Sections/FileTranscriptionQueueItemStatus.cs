namespace TypeWhisper.Linux.ViewModels.Sections;

public enum FileTranscriptionQueueItemStatus
{
    Queued,
    Loading,
    Transcribing,
    Completed,
    Cancelled,
    Error,
    Unsupported
}
