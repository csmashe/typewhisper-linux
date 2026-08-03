using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

public enum WatchFolderOutputFormat
{
    Markdown,
    PlainText,
    Srt,
    Vtt,
}

public sealed record WatchFolderOptions(
    string WatchPath,
    string? OutputPath,
    WatchFolderOutputFormat OutputFormat,
    bool DeleteSource
);

public sealed record WatchFolderTranscriptionRequest(string FilePath);

public sealed record WatchFolderTranscriptionResult(
    string Text,
    // ReSharper disable once NotAccessedPositionalProperty.Global  carried in the transcription result record's data shape
    string? DetectedLanguage,
    // ReSharper disable once NotAccessedPositionalProperty.Global  carried in the transcription result record's data shape
    double Duration,
    // ReSharper disable once NotAccessedPositionalProperty.Global  carried in the transcription result record's data shape
    double ProcessingTime,
    IReadOnlyList<TranscriptionSegment> Segments,
    string? EngineId,
    string? ModelId
);

public sealed record WatchFolderExportArtifact(string FileExtension, string Content);

public sealed record WatchFolderHistoryItem
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  get read by the reflection JSON serializer when persisting watch-folder history (WatchFolderService.SaveHistory)
    public required string Id { get; init; }
    public required string FileName { get; init; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global  get read by the reflection JSON serializer when persisting watch-folder history (WatchFolderService.SaveHistory)
    public required DateTime ProcessedAtUtc { get; init; }
    public required string OutputPath { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class WatchFolderOutputFormats
{
    public static WatchFolderOutputFormat Parse(string? storedValue)
    {
        return string.Equals(storedValue, "txt", StringComparison.OrdinalIgnoreCase)
            ? WatchFolderOutputFormat.PlainText
            : string.Equals(storedValue, "srt", StringComparison.OrdinalIgnoreCase)
                ? WatchFolderOutputFormat.Srt
                : string.Equals(storedValue, "vtt", StringComparison.OrdinalIgnoreCase)
                    ? WatchFolderOutputFormat.Vtt
                    : WatchFolderOutputFormat.Markdown;
    }

    // ReSharper disable once UnusedMember.Global  inverse of WatchFolderOutputFormats.Parse for the stored-value vocabulary (md/txt/srt/vtt); not currently called in-tree
    public static string ToStoredValue(WatchFolderOutputFormat format)
    {
        return format switch
        {
            WatchFolderOutputFormat.PlainText => "txt",
            WatchFolderOutputFormat.Srt => "srt",
            WatchFolderOutputFormat.Vtt => "vtt",
            _ => "md",
        };
    }
}