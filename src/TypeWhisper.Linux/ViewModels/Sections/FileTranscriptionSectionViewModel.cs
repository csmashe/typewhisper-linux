using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;

// ReSharper disable UnusedParameterInPartialMethod

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class FileTranscriptionSectionViewModel : ObservableObject
{
    private const string DefaultSelectionId = "__default__";
    private readonly AudioFileService _audioFiles;

    private readonly IFileTranscriptionProcessor _processor;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    // One concurrent transcription at a time — shared between manual queue
    // and the watch folder so they don't race over the model.
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private readonly WatchFolderService _watchFolder;

    [ObservableProperty]
    private string? _currentlyProcessingWatchFile;

    [ObservableProperty]
    private string? _detectedLanguage;

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private string? _fileTranscriptionEngineOverride;

    [ObservableProperty]
    private string? _fileTranscriptionModelOverride;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _isDragOver;

    // Suppresses the SaveXxx callbacks while RefreshFromSettings applies bulk
    // values — prevents saving half-written settings mid-load.
    private bool _isLoadingSettings;

    [ObservableProperty]
    private bool _isProcessing;

    private bool _isProcessingQueue;

    [ObservableProperty]
    private bool _isWatchFolderRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchFolderStatusText))]
    private string? _watchFolderStartError;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private FileTranscriptionQueueItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _statusText = Loc.Instance["FileTranscription.DragOrSelectFiles"];

    [ObservableProperty]
    private bool _watchFolderAutoStart;

    [ObservableProperty]
    private bool _watchFolderDeleteSource;

    [ObservableProperty]
    private string _watchFolderLanguage = "auto";

    [ObservableProperty]
    private string _watchFolderOutputFormat = "md";

    [ObservableProperty]
    private string? _watchFolderOutputPath;

    [ObservableProperty]
    private string? _watchFolderPath;

    public FileTranscriptionSectionViewModel(
        IFileTranscriptionProcessor processor,
        ISettingsService settings,
        AudioFileService audioFiles,
        WatchFolderService watchFolder,
        PluginManager pluginManager
    )
    {
        _processor = processor;
        _settings = settings;
        _audioFiles = audioFiles;
        _watchFolder = watchFolder;
        _pluginManager = pluginManager;

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            RefreshStatusText();
        };

        RefreshFromSettings(settings.Current);
        SyncWatchFolderState();
        _settings.SettingsChanged += settingsValue =>
            Dispatcher.UIThread.Post(() => RefreshFromSettings(settingsValue));
        _watchFolder.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncWatchFolderState);
        // Item status texts and the queue summary are resolved into stored strings,
        // so re-resolve them when the user switches UI language at runtime.
        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    public ObservableCollection<FileTranscriptionQueueItemViewModel> Items { get; } = [];

    public ObservableCollection<WatchFolderHistoryItem> WatchFolderHistory { get; } = [];

    public bool HasItems => Items.Count > 0;

    // Items in a terminal state (finished/failed/cancelled/unsupported) can be cleared
    // in bulk; active or queued items are left untouched.
    public bool HasClearableItems => Items.Any(item => IsClearableStatus(item.Status));
    public bool CanImportFiles => _audioFiles.IsImporterAvailable;
    public bool ShowImporterUnavailableReason => !CanImportFiles;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string ImporterUnavailableReason =>
        Loc.Instance["FileTranscription.ImporterUnavailableReason"];

    public bool HasWatchFolderPath => !string.IsNullOrWhiteSpace(WatchFolderPath);
    public bool HasWatchFolderOutputPath => !string.IsNullOrWhiteSpace(WatchFolderOutputPath);
    public bool HasWatchFolderHistory => WatchFolderHistory.Count > 0;
    public bool IsWatchFolderStopped => !IsWatchFolderRunning;

    internal void TryAutoStartWatchFolder()
    {
        if (WatchFolderAutoStart && HasWatchFolderPath)
        {
            TryStartWatchFolder();
        }
    }

    public string WatchFolderOutputPathDisplay =>
        HasWatchFolderOutputPath
            ? WatchFolderOutputPath!
            : Loc.Instance["FileTranscription.SameAsWatchFolder"];

    public string WatchFolderStatusText
    {
        get
        {
            if (IsWatchFolderRunning && !string.IsNullOrWhiteSpace(CurrentlyProcessingWatchFile))
            {
                return Loc.Instance.GetString(
                    "FileTranscription.ProcessingFile",
                    CurrentlyProcessingWatchFile
                );
            }

            if (!IsWatchFolderRunning && WatchFolderStartError is not null)
            {
                return Loc.Instance.GetString(
                    "FileTranscription.WatchFolderStartFailed",
                    WatchFolderStartError
                );
            }

            return IsWatchFolderRunning
                ? Loc.Instance["FileTranscription.WatchingForNewFiles"]
                : Loc.Instance["FileTranscription.Stopped"];
        }
    }

    public void HandleFileDrop(IReadOnlyList<string> files)
    {
        AddFiles(files);
    }

    public string BuildExportText()
    {
        return SelectedItem?.ResultText ?? ResultText;
    }

    public static string BuildExportText(FileTranscriptionQueueItemViewModel item)
    {
        return item.ResultText;
    }

    public string? GetExportBaseName(FileTranscriptionQueueItemViewModel? item = null)
    {
        var filePath = item?.FilePath ?? SelectedItem?.FilePath ?? FilePath;
        return string.IsNullOrWhiteSpace(filePath)
            ? null
            : Path.GetFileNameWithoutExtension(filePath);
    }

    public static string? BuildSubtitleExport(FileTranscriptionQueueItemViewModel item, string extension)
    {
        if (item.RawResult?.Segments is not { Count: > 0 } segments)
        {
            return null;
        }

        return extension == "srt"
            ? SubtitleExporter.ToSrt(segments)
            : SubtitleExporter.ToWebVtt(segments);
    }

    public void SetWatchFolderPath(string path)
    {
        WatchFolderPath = path;
    }

    public void SetWatchFolderOutputPath(string path)
    {
        WatchFolderOutputPath = path;
    }

    [RelayCommand]
    private void AddFiles(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return;
        }

        var addedSupported = false;
        foreach (
            var path in paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
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
        {
            _ = ProcessQueueAsync();
        }
    }

    [RelayCommand]
    private void TranscribeFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddFiles([path]);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var item in Items.Where(item => item.CanCancel).ToList())
        {
            CancelItem(item);
        }
    }

    [RelayCommand]
    private void CancelItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item is null || !item.CanCancel)
        {
            return;
        }

        if (item.Status == FileTranscriptionQueueItemStatus.Queued)
        {
            SetStatus(
                item,
                FileTranscriptionQueueItemStatus.Cancelled,
                Loc.Instance["FileTranscription.Cancelled"]
            );
            RefreshStatusText();
            return;
        }

        item.Cancellation?.Cancel();
    }

    [RelayCommand]
    private void RemoveItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item is null || item.IsProcessing)
        {
            return;
        }

        Items.Remove(item);
        if (SelectedItem == item)
        {
            SelectedItem = Items.FirstOrDefault();
        }

        RefreshSelectedItemResult();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        // Remove only terminal items; leave queued/loading/transcribing work running.
        foreach (var item in Items.Where(item => IsClearableStatus(item.Status)).ToList())
        {
            Items.Remove(item);
        }

        if (SelectedItem is null || !Items.Contains(SelectedItem))
        {
            SelectedItem = Items.FirstOrDefault();
        }

        RefreshSelectedItemResult();
    }

    private static bool IsClearableStatus(FileTranscriptionQueueItemStatus status) =>
        status is FileTranscriptionQueueItemStatus.Completed
            or FileTranscriptionQueueItemStatus.Cancelled
            or FileTranscriptionQueueItemStatus.Error
            or FileTranscriptionQueueItemStatus.Unsupported;

    private async Task ProcessQueueAsync()
    {
        if (_isProcessingQueue)
        {
            return;
        }

        _isProcessingQueue = true;
        IsProcessing = true;

        try
        {
            while (
                Items.FirstOrDefault(i => i.Status == FileTranscriptionQueueItemStatus.Queued)
                is { } item
            )
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
                        item.Cancellation.Token
                    );
                    item.RawResult = result.RawResult;
                    item.ResultText = result.ProcessedText;
                    item.DetectedLanguage = result.RawResult.DetectedLanguage;
                    item.ProcessingTime = result.RawResult.ProcessingTime;
                    item.AudioDuration = result.RawResult.Duration;
                    item.RefreshExportState();
                    SetStatus(
                        item,
                        FileTranscriptionQueueItemStatus.Completed,
                        Loc.Instance.GetString(
                            "FileTranscription.DoneIn",
                            result.RawResult.ProcessingTime,
                            result.RawResult.Duration
                        )
                    );
                }
                catch (OperationCanceledException)
                {
                    SetStatus(
                        item,
                        FileTranscriptionQueueItemStatus.Cancelled,
                        Loc.Instance["FileTranscription.Cancelled"]
                    );
                }
                catch (Exception ex)
                {
                    item.ErrorText = ex.Message;
                    SetStatus(item, FileTranscriptionQueueItemStatus.Error, ex.Message);
                }
                finally
                {
                    if (gateHeld)
                    {
                        _transcriptionGate.Release();
                    }

                    item.Cancellation?.Dispose();
                    item.Cancellation = null;
                    RefreshSelectedItemResult();
                }
            }
        }
        finally
        {
            // Set directly — SetStatus posts Completed asynchronously, so the
            // last item still reads as Transcribing if we check item.IsProcessing.
            _isProcessingQueue = false;
            IsProcessing = false;
            RefreshStatusText();
        }
    }

    private FileTranscriptionProcessOptions BuildFileTranscriptionOptions()
    {
        var s = _settings.Current;
        var language = s.Language == "auto" ? null : s.Language;
        var task =
            s.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;

        return new FileTranscriptionProcessOptions(
            CleanSettingValue(FileTranscriptionEngineOverride),
            CleanSettingValue(FileTranscriptionModelOverride),
            language,
            task
        );
    }

    private void SetStatus(
        FileTranscriptionQueueItemViewModel item,
        FileTranscriptionQueueItemStatus status,
        string statusText
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            item.Status = status;
            item.StatusText = statusText;
            RefreshStatusText();
            if (SelectedItem == item)
            {
                RefreshSelectedItemResult();
            }
        });
    }

    private void OnLanguageChanged()
    {
        foreach (var item in Items)
        {
            item.RefreshLocalizedText();
        }

        // Also re-resolves the section-level summary / "drag or select files" text.
        RefreshStatusText();

        // The remaining localized labels are computed getters bound via {Binding},
        // so nudge them to re-read from Loc in the new language.
        OnPropertyChanged(nameof(ImporterUnavailableReason));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        OnPropertyChanged(nameof(WatchFolderStatusText));
    }

    private void RefreshStatusText()
    {
        // Re-evaluated here because every add/remove (CollectionChanged) and every
        // status transition (SetStatus) routes through this method.
        OnPropertyChanged(nameof(HasClearableItems));

        var total = Items.Count;
        if (total == 0)
        {
            StatusText = Loc.Instance["FileTranscription.DragOrSelectFiles"];
            return;
        }

        var completed = Items.Count(item =>
            item.Status == FileTranscriptionQueueItemStatus.Completed
        );
        var failed = Items.Count(item =>
            item.Status
                is FileTranscriptionQueueItemStatus.Error
                or FileTranscriptionQueueItemStatus.Unsupported
        );
        var cancelled = Items.Count(item =>
            item.Status == FileTranscriptionQueueItemStatus.Cancelled
        );
        var queued = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Queued);
        StatusText = Loc.Instance.GetString(
            "FileTranscription.QueueSummary",
            completed,
            failed,
            cancelled,
            queued,
            total
        );
    }

    partial void OnSelectedItemChanged(FileTranscriptionQueueItemViewModel? value)
    {
        RefreshSelectedItemResult();
    }

    private void RefreshSelectedItemResult()
    {
        var item = SelectedItem;
        FilePath = item?.FilePath;
        ResultText = item?.ResultText ?? "";
        DetectedLanguage = item?.DetectedLanguage;
        HasResult = item?.HasResult == true;
    }

    [RelayCommand]
    private void ClearWatchFolderOutputPath()
    {
        WatchFolderOutputPath = null;
    }

    [RelayCommand]
    private void StartWatchFolder()
    {
        if (string.IsNullOrWhiteSpace(WatchFolderPath))
        {
            return;
        }

        TryStartWatchFolder();
    }

    [RelayCommand]
    private void StopWatchFolder()
    {
        _watchFolder.Stop();
        WatchFolderStartError = null;
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
        CancellationToken ct
    )
    {
        var options = BuildWatchFolderProcessOptions();
        ThrowIfWatchFolderNotReady(options);

        await _transcriptionGate.WaitAsync(ct);
        try
        {
            var result = await _processor.ProcessAsync(
                request.FilePath,
                _ => { },
                options,
                ct
            );

            return new WatchFolderTranscriptionResult(
                result.ProcessedText,
                result.RawResult.DetectedLanguage,
                result.RawResult.Duration,
                result.RawResult.ProcessingTime,
                result.RawResult.Segments,
                CleanSettingValue(_settings.Current.WatchFolderEngineOverride),
                CleanSettingValue(_settings.Current.WatchFolderModelOverride)
            );
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    private void ThrowIfWatchFolderNotReady(FileTranscriptionProcessOptions options)
    {
        var engines = _pluginManager.TranscriptionEngines;
        if (engines.Count == 0)
        {
            throw new WatchFolderNotReadyException(
                "Transcription engines are not ready."
            );
        }

        if (
            !string.IsNullOrWhiteSpace(options.EngineId)
            && engines.All(engine =>
                !string.Equals(
                    engine.ProviderId,
                    options.EngineId,
                    StringComparison.OrdinalIgnoreCase
                )
                && !string.Equals(
                    engine.PluginId,
                    options.EngineId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            throw new WatchFolderNotReadyException(
                $"Transcription engine '{options.EngineId}' is not ready."
            );
        }
    }

    private FileTranscriptionProcessOptions BuildWatchFolderProcessOptions()
    {
        var s = _settings.Current;
        var language =
            string.IsNullOrWhiteSpace(s.WatchFolderLanguage) || s.WatchFolderLanguage == "auto"
                ? null
                : s.WatchFolderLanguage;

        return new FileTranscriptionProcessOptions(
            CleanSettingValue(s.WatchFolderEngineOverride),
            CleanSettingValue(s.WatchFolderModelOverride),
            language,
            TranscriptionTask.Transcribe
        );
    }

    private WatchFolderOptions BuildWatchFolderOptions()
    {
        return new WatchFolderOptions(
            WatchFolderPath!,
            CleanSettingValue(WatchFolderOutputPath),
            WatchFolderOutputFormats.Parse(WatchFolderOutputFormat),
            WatchFolderDeleteSource
        );
    }

    private void TryStartWatchFolder()
    {
        try
        {
            _watchFolder.Start(BuildWatchFolderOptions(), TranscribeWatchFolderFileAsync);
            WatchFolderStartError = null;
        }
        catch (Exception ex)
        {
            // Stale mount, revoked access, invalid path, or dir replaced by a file —
            // leave the watcher stopped and surface a repairable status.
            WatchFolderStartError = ex.Message;
        }
        finally
        {
            SyncWatchFolderState();
        }
    }

    private void RestartWatchFolderIfRunning()
    {
        if (!_watchFolder.IsRunning || string.IsNullOrWhiteSpace(WatchFolderPath))
        {
            return;
        }

        TryStartWatchFolder();
    }

    private void RefreshFromSettings(AppSettings settings)
    {
        _isLoadingSettings = true;
        FileTranscriptionEngineOverride = settings.FileTranscriptionEngineOverride;
        FileTranscriptionModelOverride = settings.FileTranscriptionModelOverride;
        WatchFolderPath = settings.WatchFolderPath;
        WatchFolderOutputPath = settings.WatchFolderOutputPath;
        WatchFolderOutputFormat = string.IsNullOrWhiteSpace(settings.WatchFolderOutputFormat)
            ? "md"
            : settings.WatchFolderOutputFormat;
        WatchFolderAutoStart = settings.WatchFolderAutoStart;
        WatchFolderDeleteSource = settings.WatchFolderDeleteSource;
        WatchFolderLanguage = string.IsNullOrWhiteSpace(settings.WatchFolderLanguage)
            ? "auto"
            : settings.WatchFolderLanguage;
        _isLoadingSettings = false;

        OnPropertyChanged(nameof(HasWatchFolderPath));
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
    }

    partial void OnFileTranscriptionEngineOverrideChanged(string? value)
    {
        SaveFileTranscriptionSettings();
    }

    partial void OnFileTranscriptionModelOverrideChanged(string? value)
    {
        SaveFileTranscriptionSettings();
    }

    partial void OnWatchFolderPathChanged(string? value)
    {
        WatchFolderStartError = null;
        OnPropertyChanged(nameof(HasWatchFolderPath));
        SaveWatchFolderSettings(true);
    }

    partial void OnWatchFolderOutputPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        SaveWatchFolderSettings(true);
    }

    partial void OnWatchFolderOutputFormatChanged(string value)
    {
        SaveWatchFolderSettings(true);
    }

    partial void OnWatchFolderAutoStartChanged(bool value)
    {
        SaveWatchFolderSettings(false);
    }

    partial void OnWatchFolderDeleteSourceChanged(bool value)
    {
        SaveWatchFolderSettings(true);
    }

    partial void OnWatchFolderLanguageChanged(string value)
    {
        SaveWatchFolderSettings(false);
    }

    partial void OnIsWatchFolderRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWatchFolderStopped));
        OnPropertyChanged(nameof(WatchFolderStatusText));
    }

    private void SaveFileTranscriptionSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _settings.Save(
            _settings.Current with
            {
                FileTranscriptionEngineOverride = CleanSettingValue(
                    FileTranscriptionEngineOverride
                ),
                FileTranscriptionModelOverride = CleanSettingValue(FileTranscriptionModelOverride),
            }
        );
    }

    private void SaveWatchFolderSettings(bool restartIfRunning)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _settings.Save(
            _settings.Current with
            {
                WatchFolderPath = CleanSettingValue(WatchFolderPath),
                WatchFolderOutputPath = CleanSettingValue(WatchFolderOutputPath),
                WatchFolderOutputFormat = string.IsNullOrWhiteSpace(WatchFolderOutputFormat)
                    ? "md"
                    : WatchFolderOutputFormat,
                WatchFolderAutoStart = WatchFolderAutoStart,
                WatchFolderDeleteSource = WatchFolderDeleteSource,
                WatchFolderLanguage = string.IsNullOrWhiteSpace(WatchFolderLanguage)
                    ? "auto"
                    : WatchFolderLanguage,
            }
        );

        if (restartIfRunning)
        {
            RestartWatchFolderIfRunning();
        }
    }

    private void SyncWatchFolderState()
    {
        IsWatchFolderRunning = _watchFolder.IsRunning;
        CurrentlyProcessingWatchFile = _watchFolder.CurrentlyProcessing;
        WatchFolderHistory.Clear();
        foreach (var item in _watchFolder.History)
        {
            WatchFolderHistory.Add(item);
        }

        OnPropertyChanged(nameof(WatchFolderStatusText));
        OnPropertyChanged(nameof(HasWatchFolderHistory));
    }

    // Normalises ComboBox placeholder values ("__default__" and whitespace)
    // to null so they're never persisted as a real engine/model override.
    private static string? CleanSettingValue(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) || cleaned == DefaultSelectionId ? null : cleaned;
    }
}
