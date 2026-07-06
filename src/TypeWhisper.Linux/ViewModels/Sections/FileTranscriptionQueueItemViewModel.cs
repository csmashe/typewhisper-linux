using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public sealed partial class FileTranscriptionQueueItemViewModel : ObservableObject
{
    [ObservableProperty]
    private double _audioDuration;

    [ObservableProperty]
    private string? _detectedLanguage;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private double _processingTime;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private FileTranscriptionQueueItemStatus _status;

    [ObservableProperty]
    private string _statusText;

    public FileTranscriptionQueueItemViewModel(
        string filePath,
        FileTranscriptionQueueItemStatus status
    )
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Status = status;
        StatusText =
            status == FileTranscriptionQueueItemStatus.Unsupported
                ? Loc.Instance["FileTranscription.UnsupportedFormat"]
                : Loc.Instance["FileTranscription.Queued"];
        ErrorText = status == FileTranscriptionQueueItemStatus.Unsupported ? StatusText : "";
    }

    public string FilePath { get; }
    public string FileName { get; }
    public CancellationTokenSource? Cancellation { get; set; }
    public TranscriptionResult? RawResult { get; set; }

    public bool IsProcessing =>
        Status
            is FileTranscriptionQueueItemStatus.Loading
            or FileTranscriptionQueueItemStatus.Transcribing;

    public bool CanCancel =>
        Status
            is FileTranscriptionQueueItemStatus.Queued
            or FileTranscriptionQueueItemStatus.Loading
            or FileTranscriptionQueueItemStatus.Transcribing;

    public bool HasResult =>
        Status == FileTranscriptionQueueItemStatus.Completed
        && !string.IsNullOrWhiteSpace(ResultText);

    public bool CanExportSubtitles => HasResult && RawResult?.Segments is { Count: > 0 };
    public bool HasDetectedLanguage => !string.IsNullOrWhiteSpace(DetectedLanguage);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public void RefreshExportState()
    {
        OnPropertyChanged(nameof(CanExportSubtitles));
    }

    /// <summary>
    ///     Re-resolves the status text for statuses whose label is a pure function
    ///     of <see cref="Status" /> (plus stored timing), so a live UI-language
    ///     switch updates queued and terminal items. Loading/Transcribing carry
    ///     the processor's transient progress text and Error carries a raw
    ///     exception message, so both are intentionally left untouched.
    /// </summary>
    public void RefreshLocalizedText()
    {
        // Loading/Transcribing/Error carry transient progress or raw
        // exception text and are intentionally left untouched (see summary).
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- only the actionable cases are handled; remaining enum values are deliberate no-ops.
        switch (Status)
        {
            case FileTranscriptionQueueItemStatus.Unsupported:
                StatusText = Loc.Instance["FileTranscription.UnsupportedFormat"];
                ErrorText = StatusText;
                break;
            case FileTranscriptionQueueItemStatus.Queued:
                StatusText = Loc.Instance["FileTranscription.Queued"];
                break;
            case FileTranscriptionQueueItemStatus.Cancelled:
                StatusText = Loc.Instance["FileTranscription.Cancelled"];
                break;
            case FileTranscriptionQueueItemStatus.Completed:
                StatusText = Loc.Instance.GetString(
                    "FileTranscription.DoneIn",
                    ProcessingTime,
                    AudioDuration
                );
                break;
        }
    }

    partial void OnStatusChanged(FileTranscriptionQueueItemStatus value)
    {
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(CanExportSubtitles));
    }

    partial void OnResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(CanExportSubtitles));
    }

    partial void OnDetectedLanguageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDetectedLanguage));
    }

    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }
}