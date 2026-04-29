using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class FileTranscriptionSectionViewModel : ObservableObject
{
    private const string DefaultSelectionId = "__default__";

    private readonly IFileTranscriptionProcessor _processor;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFiles;
    private readonly WatchFolderService _watchFolder;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private bool _isProcessingQueue;
    private bool _isLoadingSettings;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = "Drag or select files";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private FileTranscriptionQueueItemViewModel? _selectedItem;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private string? _fileTranscriptionEngineOverride;
    [ObservableProperty] private string? _fileTranscriptionModelOverride;
    [ObservableProperty] private string? _watchFolderPath;
    [ObservableProperty] private string? _watchFolderOutputPath;
    [ObservableProperty] private string _watchFolderOutputFormat = "md";
    [ObservableProperty] private bool _watchFolderAutoStart;
    [ObservableProperty] private bool _watchFolderDeleteSource;
    [ObservableProperty] private string _watchFolderLanguage = "auto";
    [ObservableProperty] private bool _isWatchFolderRunning;
    [ObservableProperty] private string? _currentlyProcessingWatchFile;

    public ObservableCollection<FileTranscriptionQueueItemViewModel> Items { get; } = [];
    public ObservableCollection<WatchFolderOutputFormatOption> WatchFolderOutputFormatOptions { get; } =
    [
        new("md", "Markdown"),
        new("txt", "Text"),
        new("srt", "SRT"),
        new("vtt", "WebVTT")
    ];
    public ObservableCollection<WatchFolderHistoryItem> WatchFolderHistory { get; } = [];

    public bool HasItems => Items.Count > 0;
    public bool CanImportFiles => _audioFiles.IsImporterAvailable;
    public bool ShowImporterUnavailableReason => !CanImportFiles;
    public string ImporterUnavailableReason => "Unavailable: ffmpeg is not installed on this system.";
    public bool HasWatchFolderPath => !string.IsNullOrWhiteSpace(WatchFolderPath);
    public bool HasWatchFolderOutputPath => !string.IsNullOrWhiteSpace(WatchFolderOutputPath);
    public bool HasWatchFolderHistory => WatchFolderHistory.Count > 0;
    public bool IsWatchFolderStopped => !IsWatchFolderRunning;
    public string WatchFolderOutputPathDisplay => HasWatchFolderOutputPath
        ? WatchFolderOutputPath!
        : "Same as watch folder";
    public string WatchFolderStatusText
    {
        get
        {
            if (IsWatchFolderRunning && !string.IsNullOrWhiteSpace(CurrentlyProcessingWatchFile))
                return $"Processing {CurrentlyProcessingWatchFile}";

            return IsWatchFolderRunning ? "Watching for new files" : "Stopped";
        }
    }

    public FileTranscriptionSectionViewModel(
        IFileTranscriptionProcessor processor,
        ISettingsService settings,
        AudioFileService audioFiles,
        WatchFolderService watchFolder)
    {
        _processor = processor;
        _settings = settings;
        _audioFiles = audioFiles;
        _watchFolder = watchFolder;

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            RefreshStatusText();
        };

        RefreshFromSettings(settings.Current);
        SyncWatchFolderState();
        _settings.SettingsChanged += settingsValue => Dispatcher.UIThread.Post(() => RefreshFromSettings(settingsValue));
        _watchFolder.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncWatchFolderState);

        if (WatchFolderAutoStart && HasWatchFolderPath)
            StartWatchFolder();
    }

    [RelayCommand]
    private void AddFiles(IEnumerable<string>? paths)
    {
        if (paths is null)
            return;

        var addedSupported = false;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var status = AudioFileService.IsSupported(path)
                ? FileTranscriptionQueueItemStatus.Queued
                : FileTranscriptionQueueItemStatus.Unsupported;
            var item = new FileTranscriptionQueueItemViewModel(path, status);
            Items.Add(item);
            SelectedItem ??= item;
            addedSupported |= status == FileTranscriptionQueueItemStatus.Queued;
        }

        if (addedSupported)
            _ = ProcessQueueAsync();
    }

    [RelayCommand]
    private void TranscribeFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            AddFiles([path]);
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var item in Items.Where(item => item.CanCancel).ToList())
            CancelItem(item);
    }

    [RelayCommand]
    private void CancelItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item is null || !item.CanCancel)
            return;

        if (item.Status == FileTranscriptionQueueItemStatus.Queued)
        {
            SetStatus(item, FileTranscriptionQueueItemStatus.Cancelled, "Cancelled.");
            RefreshStatusText();
            return;
        }

        item.Cancellation?.Cancel();
    }

    [RelayCommand]
    private void RemoveItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item is null || item.IsProcessing)
            return;

        Items.Remove(item);
        if (SelectedItem == item)
            SelectedItem = Items.FirstOrDefault();
        RefreshSelectedItemResult();
    }

    public void HandleFileDrop(IReadOnlyList<string> files) => AddFiles(files);

    private async Task ProcessQueueAsync()
    {
        if (_isProcessingQueue)
            return;

        _isProcessingQueue = true;
        IsProcessing = true;

        try
        {
            while (Items.FirstOrDefault(item => item.Status == FileTranscriptionQueueItemStatus.Queued) is { } item)
            {
                SelectedItem = item;
                item.Cancellation = new CancellationTokenSource();
                var gateHeld = false;

                try
                {
                    await _transcriptionGate.WaitAsync(item.Cancellation.Token);
                    gateHeld = true;

                    var result = await _processor.ProcessAsync(
                        item.FilePath,
                        progress => SetStatus(item, progress.Status, progress.StatusText),
                        BuildFileTranscriptionOptions(),
                        item.Cancellation.Token);
                    item.RawResult = result.RawResult;
                    item.ResultText = result.ProcessedText;
                    item.DetectedLanguage = result.RawResult.DetectedLanguage;
                    item.ProcessingTime = result.RawResult.ProcessingTime;
                    item.AudioDuration = result.RawResult.Duration;
                    item.RefreshExportState();
                    SetStatus(item, FileTranscriptionQueueItemStatus.Completed,
                        $"Done in {result.RawResult.ProcessingTime:F1}s ({result.RawResult.Duration:F1}s audio)");
                }
                catch (OperationCanceledException)
                {
                    SetStatus(item, FileTranscriptionQueueItemStatus.Cancelled, "Cancelled.");
                }
                catch (Exception ex)
                {
                    item.ErrorText = ex.Message;
                    SetStatus(item, FileTranscriptionQueueItemStatus.Error, ex.Message);
                }
                finally
                {
                    if (gateHeld)
                        _transcriptionGate.Release();

                    item.Cancellation?.Dispose();
                    item.Cancellation = null;
                    RefreshSelectedItemResult();
                }
            }
        }
        finally
        {
            _isProcessingQueue = false;
            IsProcessing = Items.Any(item => item.IsProcessing);
            RefreshStatusText();
        }
    }

    private FileTranscriptionProcessOptions BuildFileTranscriptionOptions()
    {
        var s = _settings.Current;
        var language = s.Language == "auto" ? null : s.Language;
        var task = s.TranscriptionTask == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;

        return new FileTranscriptionProcessOptions(
            CleanSettingValue(FileTranscriptionEngineOverride),
            CleanSettingValue(FileTranscriptionModelOverride),
            language,
            task);
    }

    private void SetStatus(FileTranscriptionQueueItemViewModel item, FileTranscriptionQueueItemStatus status, string statusText)
    {
        Dispatcher.UIThread.Post(() =>
        {
            item.Status = status;
            item.StatusText = statusText;
            RefreshStatusText();
            if (SelectedItem == item)
                RefreshSelectedItemResult();
        });
    }

    private void RefreshStatusText()
    {
        var total = Items.Count;
        if (total == 0)
        {
            StatusText = "Drag or select files";
            return;
        }

        var completed = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Completed);
        var failed = Items.Count(item => item.Status is FileTranscriptionQueueItemStatus.Error or FileTranscriptionQueueItemStatus.Unsupported);
        var cancelled = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Cancelled);
        var queued = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Queued);
        StatusText = $"{completed} complete, {failed} failed, {cancelled} cancelled, {queued} queued ({total} total)";
    }

    partial void OnSelectedItemChanged(FileTranscriptionQueueItemViewModel? value) => RefreshSelectedItemResult();

    private void RefreshSelectedItemResult()
    {
        var item = SelectedItem;
        FilePath = item?.FilePath;
        ResultText = item?.ResultText ?? "";
        DetectedLanguage = item?.DetectedLanguage;
        HasResult = item?.HasResult == true;
    }

    public string BuildExportText() => SelectedItem?.ResultText ?? ResultText;

    public string BuildExportText(FileTranscriptionQueueItemViewModel item) => item.ResultText;

    public string? GetExportBaseName(FileTranscriptionQueueItemViewModel? item = null)
    {
        var filePath = item?.FilePath ?? SelectedItem?.FilePath ?? FilePath;
        return string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFileNameWithoutExtension(filePath);
    }

    public string? BuildSubtitleExport(FileTranscriptionQueueItemViewModel item, string extension)
    {
        if (item.RawResult?.Segments is not { Count: > 0 } segments)
            return null;

        return extension == "srt"
            ? SubtitleExporter.ToSrt(segments)
            : SubtitleExporter.ToWebVtt(segments);
    }

    public void SetWatchFolderPath(string path) => WatchFolderPath = path;
    public void SetWatchFolderOutputPath(string path) => WatchFolderOutputPath = path;

    [RelayCommand]
    private void ClearWatchFolderOutputPath() => WatchFolderOutputPath = null;

    [RelayCommand]
    private void StartWatchFolder()
    {
        if (string.IsNullOrWhiteSpace(WatchFolderPath))
            return;

        _watchFolder.Start(BuildWatchFolderOptions(), TranscribeWatchFolderFileAsync);
        SyncWatchFolderState();
    }

    [RelayCommand]
    private void StopWatchFolder()
    {
        _watchFolder.Stop();
        SyncWatchFolderState();
    }

    [RelayCommand]
    private void ClearWatchFolderHistory()
    {
        _watchFolder.ClearHistory();
        SyncWatchFolderState();
    }

    private async Task<WatchFolderTranscriptionResult> TranscribeWatchFolderFileAsync(
        WatchFolderTranscriptionRequest request,
        CancellationToken ct)
    {
        await _transcriptionGate.WaitAsync(ct);
        try
        {
            var result = await _processor.ProcessAsync(
                request.FilePath,
                _ => { },
                BuildWatchFolderProcessOptions(),
                ct);

            return new WatchFolderTranscriptionResult(
                result.ProcessedText,
                result.RawResult.DetectedLanguage,
                result.RawResult.Duration,
                result.RawResult.ProcessingTime,
                result.RawResult.Segments,
                CleanSettingValue(_settings.Current.WatchFolderEngineOverride),
                CleanSettingValue(_settings.Current.WatchFolderModelOverride));
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    private FileTranscriptionProcessOptions BuildWatchFolderProcessOptions()
    {
        var s = _settings.Current;
        var language = string.IsNullOrWhiteSpace(s.WatchFolderLanguage) || s.WatchFolderLanguage == "auto"
            ? null
            : s.WatchFolderLanguage;

        return new FileTranscriptionProcessOptions(
            CleanSettingValue(s.WatchFolderEngineOverride),
            CleanSettingValue(s.WatchFolderModelOverride),
            language,
            TranscriptionTask.Transcribe);
    }

    private WatchFolderOptions BuildWatchFolderOptions() =>
        new(
            WatchFolderPath!,
            CleanSettingValue(WatchFolderOutputPath),
            WatchFolderOutputFormats.Parse(WatchFolderOutputFormat),
            WatchFolderDeleteSource);

    private void RestartWatchFolderIfRunning()
    {
        if (!_watchFolder.IsRunning || string.IsNullOrWhiteSpace(WatchFolderPath))
            return;

        _watchFolder.Start(BuildWatchFolderOptions(), TranscribeWatchFolderFileAsync);
        SyncWatchFolderState();
    }

    private void RefreshFromSettings(AppSettings settings)
    {
        _isLoadingSettings = true;
        FileTranscriptionEngineOverride = settings.FileTranscriptionEngineOverride;
        FileTranscriptionModelOverride = settings.FileTranscriptionModelOverride;
        WatchFolderPath = settings.WatchFolderPath;
        WatchFolderOutputPath = settings.WatchFolderOutputPath;
        WatchFolderOutputFormat = string.IsNullOrWhiteSpace(settings.WatchFolderOutputFormat) ? "md" : settings.WatchFolderOutputFormat;
        WatchFolderAutoStart = settings.WatchFolderAutoStart;
        WatchFolderDeleteSource = settings.WatchFolderDeleteSource;
        WatchFolderLanguage = string.IsNullOrWhiteSpace(settings.WatchFolderLanguage) ? "auto" : settings.WatchFolderLanguage;
        _isLoadingSettings = false;

        OnPropertyChanged(nameof(HasWatchFolderPath));
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
    }

    partial void OnFileTranscriptionEngineOverrideChanged(string? value) => SaveFileTranscriptionSettings();
    partial void OnFileTranscriptionModelOverrideChanged(string? value) => SaveFileTranscriptionSettings();

    partial void OnWatchFolderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWatchFolderPath));
        SaveWatchFolderSettings(restartIfRunning: true);
    }

    partial void OnWatchFolderOutputPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        SaveWatchFolderSettings(restartIfRunning: true);
    }

    partial void OnWatchFolderOutputFormatChanged(string value) => SaveWatchFolderSettings(restartIfRunning: true);
    partial void OnWatchFolderAutoStartChanged(bool value) => SaveWatchFolderSettings(restartIfRunning: false);
    partial void OnWatchFolderDeleteSourceChanged(bool value) => SaveWatchFolderSettings(restartIfRunning: true);
    partial void OnWatchFolderLanguageChanged(string value) => SaveWatchFolderSettings(restartIfRunning: false);

    partial void OnIsWatchFolderRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWatchFolderStopped));
        OnPropertyChanged(nameof(WatchFolderStatusText));
    }

    private void SaveFileTranscriptionSettings()
    {
        if (_isLoadingSettings)
            return;

        _settings.Save(_settings.Current with
        {
            FileTranscriptionEngineOverride = CleanSettingValue(FileTranscriptionEngineOverride),
            FileTranscriptionModelOverride = CleanSettingValue(FileTranscriptionModelOverride)
        });
    }

    private void SaveWatchFolderSettings(bool restartIfRunning)
    {
        if (_isLoadingSettings)
            return;

        _settings.Save(_settings.Current with
        {
            WatchFolderPath = CleanSettingValue(WatchFolderPath),
            WatchFolderOutputPath = CleanSettingValue(WatchFolderOutputPath),
            WatchFolderOutputFormat = string.IsNullOrWhiteSpace(WatchFolderOutputFormat) ? "md" : WatchFolderOutputFormat,
            WatchFolderAutoStart = WatchFolderAutoStart,
            WatchFolderDeleteSource = WatchFolderDeleteSource,
            WatchFolderLanguage = string.IsNullOrWhiteSpace(WatchFolderLanguage) ? "auto" : WatchFolderLanguage
        });

        if (restartIfRunning)
            RestartWatchFolderIfRunning();
    }

    private void SyncWatchFolderState()
    {
        IsWatchFolderRunning = _watchFolder.IsRunning;
        CurrentlyProcessingWatchFile = _watchFolder.CurrentlyProcessing;
        WatchFolderHistory.Clear();
        foreach (var item in _watchFolder.History)
            WatchFolderHistory.Add(item);

        OnPropertyChanged(nameof(WatchFolderStatusText));
        OnPropertyChanged(nameof(HasWatchFolderHistory));
    }

    private static string? CleanSettingValue(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) || cleaned == DefaultSelectionId ? null : cleaned;
    }
}

public sealed record WatchFolderOutputFormatOption(string Id, string DisplayName);
