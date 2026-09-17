using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Timer = System.Timers.Timer;

namespace TypeWhisper.Linux.ViewModels.Sections;

public sealed partial class RecordingItem(
    string fileName,
    string filePath,
    DateTime createdAt,
    TimeSpan duration,
    string? transcript
) : ObservableObject
{
    public string FileName { get; } = fileName;
    public string FilePath { get; } = filePath;
    public DateTime CreatedAt { get; } = createdAt;
    // ReSharper disable once UnusedMember.Global -- part of the recording model; the row template does not show it yet.
    public TimeSpan Duration { get; } = duration;
    public string? Transcript { get; } = transcript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackButtonText))]
    private bool _isPlaying;

    public string PlaybackButtonText => Loc.Instance[IsPlaying ? "Recorder.Stop" : "Recorder.Play"];
}

public partial class RecorderSectionViewModel : ObservableObject
{
    private readonly RecorderService _recorder;
    private readonly AudioPlaybackService _audioPlayback;
    private readonly Action<Action> _postToUiThread;
    private readonly string _recordingDirectory;
    private readonly Func<byte[], CancellationToken, Task<string?>> _transcribeAsync;
    private readonly CancellationTokenSource _transcriptionCancellation = new();
    private readonly Lock _workflowGate = new();
    private bool _commandIngressClosed;
    private bool _uiInitiatedSession;
    private Task _publishedWorkflow = Task.CompletedTask;
    private Task _fallbackStop = Task.CompletedTask;

    [ObservableProperty]
    private double _audioLevel;

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTranscribing;

    [ObservableProperty]
    private bool _isPaused;

    public bool IsSessionActive => IsRecording;
    public string PauseResumeButtonText => Loc.Instance[IsPaused ? "Recorder.Resume" : "Recorder.Pause"];

    [ObservableProperty]
    private string _statusText = Loc.Instance["Recorder.StatusReady"];

    private Timer? _timer;

    public RecorderSectionViewModel(
        RecorderService recorder,
        AudioPlaybackService audioPlayback,
        ModelManagerService models,
        ISettingsService settings
    )
        : this(
            recorder,
            audioPlayback,
            settings,
            TypeWhisperEnvironment.AudioPath,
            CreateTranscriptionDelegate(models, settings)
        )
    {
    }

    internal RecorderSectionViewModel(
        RecorderService recorder,
        AudioPlaybackService audioPlayback,
        ModelManagerService models,
        ISettingsService settings,
        string recordingDirectory
    )
        : this(
            recorder,
            audioPlayback,
            settings,
            recordingDirectory,
            CreateTranscriptionDelegate(models, settings)
        )
    {
    }

    internal RecorderSectionViewModel(
        RecorderService recorder,
        AudioPlaybackService audioPlayback,
        ISettingsService settings,
        string recordingDirectory,
        Func<byte[], CancellationToken, Task<string?>> transcribeAsync,
        Action<Action>? postToUiThread = null
    )
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(audioPlayback);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingDirectory);
        ArgumentNullException.ThrowIfNull(transcribeAsync);

        _recorder = recorder;
        _audioPlayback = audioPlayback;
        _postToUiThread = postToUiThread ?? (action => Dispatcher.UIThread.Post(action));
        _recordingDirectory = recordingDirectory;
        _transcribeAsync = transcribeAsync;
        _recorder.LevelChanged += (_, level) => _postToUiThread(() =>
        {
            if (IsRecording && _recorder.State == RecorderState.Recording)
            {
                AudioLevel = Math.Clamp(level * 8, 0, 1);
            }
        });
        _recorder.Changed += () => _postToUiThread(RefreshRecorderState);
        _recorder.RecordingSaved += OnRecordingSaved;
        _audioPlayback.PlaybackStateChanged += () => _postToUiThread(RefreshPlaybackState);
        RefreshRecorderState();
        LoadExistingRecordings();
    }

    public string RecordButtonText =>
        IsRecording ? Loc.Instance["Recorder.Stop"] : Loc.Instance["Recorder.Record"];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    [RelayCommand]
    private Task ToggleRecording() => TryStartWorkflow(transcribeOnStop: true) ?? Task.CompletedTask;

    // Returns null when the single workflow lane is busy or closed, so callers that
    // must not give up (the recording-limit tick) can retry instead of stalling.
    private Task? TryStartWorkflow(bool transcribeOnStop)
    {
        TaskCompletionSource publishedWorkflow;
        Task workflow;
        bool stopRecording;
        lock (_workflowGate)
        {
            if (_commandIngressClosed || !_publishedWorkflow.IsCompleted)
            {
                return null;
            }

            publishedWorkflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workflow = publishedWorkflow.Task;
            stopRecording = _recorder.IsSessionActive;
            _publishedWorkflow = workflow;
        }

        _ = RunToggleRecordingWorkflowAsync(
            publishedWorkflow,
            stopRecording,
            transcribeOnStop,
            _transcriptionCancellation.Token
        );
        return workflow;
    }

    private async Task RunToggleRecordingWorkflowAsync(
        TaskCompletionSource publishedWorkflow,
        bool stopRecording,
        bool transcribeOnStop,
        CancellationToken transcriptionCancellationToken
    )
    {
        try
        {
            if (stopRecording)
            {
                await StopRecordingAsync(transcribeOnStop, transcriptionCancellationToken);
            }
            else
            {
                await StartRecordingAsync();
            }
        }
        catch (OperationCanceledException ex)
        {
            publishedWorkflow.TrySetCanceled(ex.CancellationToken);
            return;
        }
        catch (Exception ex)
        {
            publishedWorkflow.TrySetException(ex);
            return;
        }

        publishedWorkflow.TrySetResult();
    }

    internal async Task<bool> QuiesceAsync(TimeSpan budget)
    {
        Task workflow;
        lock (_workflowGate)
        {
            _commandIngressClosed = true;
            // A limit stop that bypassed the workflow lane still has a file to write,
            // so shutdown must drain it before the audio services are disposed.
            workflow = _fallbackStop.IsCompleted
                ? _publishedWorkflow
                : Task.WhenAll(_publishedWorkflow, _fallbackStop);
        }

        _timer?.Stop();
        _audioPlayback.Stop();

        // Deliberately do not auto-stop an active recording whose stop workflow
        // was never initiated. Shutdown cancellation applies only to transcription.

        // Launch the cancel on the pool so a blocking plugin cancellation callback
        // cannot park the caller before the bounded wait below even starts.
        var cancelWorker = Task.Run(_transcriptionCancellation.Cancel);
        _ = cancelWorker.ContinueWith(
            static task =>
            {
                // Read the exception outside the conditional Debug call so the fault
                // is observed in Release builds too.
                var observed = task.Exception;
                Debug.WriteLine(
                    $"[Recorder] Transcription cancellation callback failed: {observed}"
                );
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        try
        {
            await workflow.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!workflow.IsCompleted)
        {
            // Bound stalled disk work and plugins that ignore cancellation. The
            // intact workflow may still finish, so observe any late fault.
            _ = workflow.ContinueWith(
                static task =>
                {
                    // Read the exception outside the conditional Debug call so the
                    // fault is observed in Release builds too.
                    var observed = task.Exception;
                    Debug.WriteLine(
                        $"[Recorder] Workflow failed after quiesce timed out: {observed}"
                    );
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            return false;
        }
        catch (Exception ex)
        {
            // Any other exception (including a TimeoutException that lost the race to
            // a completing workflow) means the workflow SETTLED — faulted or canceled —
            // rather than outlived the budget. The recording lane is quiet, which is
            // exactly what shutdown needs: a sticky faulted workflow (e.g. a busy mic
            // making TryStartRecording rethrow) must not force every later shutdown
            // onto the skip-all-disposal path.
            Debug.WriteLine($"[Recorder] Workflow settled non-successfully: {ex.Message}");
            return true;
        }
    }

    private async Task StartRecordingAsync()
    {
        _audioPlayback.Stop();
        bool started;
        try
        {
            started = await _recorder.StartAsync();
        }
        catch (RecorderBusyException)
        {
            // An API save was finishing while the button was live; faulting the command
            // would reach AsyncRelayCommand's rethrow and take the app down.
            RefreshRecorderState();
            return;
        }

        if (!started)
        {
            StatusText = Loc.Instance["Recorder.StatusNoMicrophone"];
        }
        else
        {
            _uiInitiatedSession = true;
            RefreshRecorderState();
        }
    }

    [RelayCommand]
    private Task PauseResume()
    {
        TaskCompletionSource completion;
        lock (_workflowGate)
        {
            if (_commandIngressClosed || !_publishedWorkflow.IsCompleted)
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _publishedWorkflow = completion.Task;
        }

        _ = RunPauseResumeAsync(completion);
        return completion.Task;
    }

    private async Task RunPauseResumeAsync(TaskCompletionSource completion)
    {
        try
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- only two of the four states toggle; a switch would need empty arms.
            if (_recorder.State == RecorderState.Recording)
            {
                await _recorder.PauseAsync();
            }
            else if (_recorder.State == RecorderState.Paused)
            {
                _audioPlayback.Stop();
                await _recorder.ResumeAsync();
            }

            RefreshRecorderState();
        }
        catch
        {
            RefreshRecorderState();
            StatusText = Loc.Instance["Recorder.StatusNoMicrophone"];
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private void RefreshRecorderState()
    {
        var state = _recorder.State;
        IsRecording = state is RecorderState.Recording or RecorderState.Paused;
        IsPaused = state == RecorderState.Paused;
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(PauseResumeButtonText));
        DurationText = state == RecorderState.Ready ? "0:00" : FormatDuration(_recorder.ActiveDuration);
        _timer?.Stop();
        if (state == RecorderState.Recording)
        {
            StatusText = Loc.Instance["Recorder.StatusRecording"];
            _timer ??= CreateTimer();
            if (!_commandIngressClosed)
            {
                _timer.Start();
            }
        }
        else
        {
            AudioLevel = 0;
            if (state == RecorderState.Ready)
            {
                _uiInitiatedSession = false;
            }

            if (IsPaused)
            {
                StatusText = Loc.Instance["Recorder.StatusPaused"];
            }
            else if (state == RecorderState.Ready && !IsTranscribing
                     && (StatusText == Loc.Instance["Recorder.StatusRecording"]
                         || StatusText == Loc.Instance["Recorder.StatusPaused"]))
            {
                StatusText = Loc.Instance["Recorder.StatusReady"];
            }
        }
    }

    private Timer CreateTimer()
    {
        var timer = new Timer(100);
        timer.Elapsed += (_, _) => _postToUiThread(() =>
        {
            if (_commandIngressClosed || _recorder.State != RecorderState.Recording)
            {
                return;
            }
            var elapsed = _recorder.ActiveDuration;
            DurationText = FormatDuration(elapsed);
            if (elapsed < RecorderService.MaximumActiveDuration)
            {
                return;
            }

            StatusText = Loc.Instance["Recorder.StatusLimitReached"];
            // An API-started session must never be transcribed: /v1/recorder/start
            // promises no automatic transcription, limit stop included.
            if (TryStartWorkflow(transcribeOnStop: _uiInitiatedSession) is not null)
            {
                timer.Stop();
            }
            else if (!_uiInitiatedSession)
            {
                // The workflow lane is still held by an earlier transcription, which
                // only an API session can outlive. Stop it straight through the
                // service so a stalled plugin cannot let capture grow without bound.
                timer.Stop();
                lock (_workflowGate)
                {
                    if (!_commandIngressClosed && _fallbackStop.IsCompleted)
                    {
                        _fallbackStop = Task.Run(StopAtLimitAsync);
                    }
                }
            }
        });
        return timer;
    }

    private async Task StopAtLimitAsync()
    {
        string status;
        try
        {
            status = await _recorder.StopAsync() is null
                ? "Recorder.StatusNoAudio"
                : "Recorder.StatusSavedNoTranscript";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Recorder] Limit stop failed: {ex}");
            status = "Recorder.StatusSaveFailed";
        }

        _postToUiThread(() => StatusText = Loc.Instance[status]);
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
        : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";

    private void OnRecordingSaved(RecorderSavedEventArgs args)
    {
        if (ReferenceEquals(args.Initiator, this))
        {
            return;
        }
        _postToUiThread(() => InsertRecording(args.Result));
    }

    private RecordingItem InsertRecording(RecorderStopResult result)
    {
        var existing = Recordings.FirstOrDefault(item => item.FilePath == result.FilePath);
        if (existing is not null)
        {
            return existing;
        }
        var item = new RecordingItem(
            Path.GetFileName(result.FilePath), result.FilePath, DateTime.Now, result.Duration, null
        );
        Recordings.Insert(0, item);
        OnPropertyChanged(nameof(HasRecordings));
        return item;
    }

    [RelayCommand]
    private void TogglePlayback(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }
        if (item.IsPlaying)
        {
            _audioPlayback.Stop();
        }
        else
        {
            _audioPlayback.Play(Path.GetRelativePath(TypeWhisperEnvironment.AudioPath, item.FilePath));
        }
    }

    private void RefreshPlaybackState()
    {
        foreach (var item in Recordings)
        {
            item.IsPlaying = _audioPlayback.IsPlaying && string.Equals(
                _audioPlayback.CurrentFile,
                Path.GetRelativePath(TypeWhisperEnvironment.AudioPath, item.FilePath),
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private async Task StopRecordingAsync(
        bool transcribe,
        CancellationToken transcriptionCancellationToken
    )
    {
        _timer?.Stop();
        RecorderStopResult? result;
        try
        {
            result = await _recorder.StopAsync(this);
            if (result is null)
            {
                StatusText = Loc.Instance["Recorder.StatusNoAudio"];
                DurationText = "0:00";
                return;
            }
        }
        catch
        {
            StatusText = Loc.Instance["Recorder.StatusSaveFailed"];
            DurationText = "0:00";
            return;
        }
        finally
        {
            RefreshRecorderState();
            AudioLevel = 0;
        }

        var pendingItem = InsertRecording(result);
        var wav = result.Wav;
        var filePath = result.FilePath;
        var duration = result.Duration;
        var fileName = Path.GetFileName(filePath);

        if (!transcribe)
        {
            StatusText = Loc.Instance["Recorder.StatusSavedNoTranscript"];
            DurationText = "0:00";
            return;
        }

        StatusText = Loc.Instance["Recorder.StatusSavedTranscribing"];
        IsTranscribing = true;

        string? transcript;
        Exception? transcriptionException = null;
        try
        {
            // Cancellation is cooperative: a plugin may ignore it and complete a
            // transcript during shutdown, while the recording remains preserved.
            transcript = await _transcribeAsync(wav, transcriptionCancellationToken);
        }
        catch (OperationCanceledException)
            when (transcriptionCancellationToken.IsCancellationRequested)
        {
            // Shutdown/teardown cancel: the recording is kept, but a cancel is not a
            // transcription failure and must not surface as one. An OCE with the token
            // NOT requested (a plugin HTTP timeout) is a dependency fault and falls
            // through to the failure arm per the SDK cancellation-origin contract.
            transcript = null;
        }
        catch (Exception ex)
        {
            // Keep the recording even if transcription fails.
            transcriptionException = ex;
            transcript = null;
        }

        var transcriptWriteFailed = false;
        var transcriptPersisted = false;
        // The row is listed while transcription runs, so Delete can remove the WAV
        // first. Writing the sidecar then would strand a .txt with no row to delete it.
        if (!string.IsNullOrWhiteSpace(transcript) && File.Exists(filePath))
        {
            try
            {
                AtomicFileWrite.WriteAllTextCreateNew(
                    Path.ChangeExtension(filePath, ".txt"),
                    transcript
                );
                transcriptPersisted = true;
            }
            catch
            {
                // Keep the recording; report the sidecar failure instead of
                // misreporting "no model loaded".
                transcriptWriteFailed = true;
            }
        }

        IsTranscribing = false;
        var index = Recordings.IndexOf(pendingItem);
        if (index >= 0)
        {
            Recordings[index] = new RecordingItem(fileName, filePath, pendingItem.CreatedAt, duration, transcript)
            {
                IsPlaying = pendingItem.IsPlaying,
            };
        }
        StatusText = transcriptWriteFailed
            ? Loc.Instance["Recorder.StatusTranscriptSaveFailed"]
            : transcriptionException is not null
                ? Loc.Instance.GetString(
                    "Recorder.StatusSavedTranscriptionFailed",
                    // Language-selection failures get the same localized message
                    // as every other surface; other exceptions keep their raw text.
                    LanguageSelectionUiMessage.From(transcriptionException)
                )
            : transcriptPersisted
                ? Loc.Instance["Recorder.StatusDone"]
                : Loc.Instance["Recorder.StatusSavedNoTranscript"];
        DurationText = "0:00";
    }

    private static Func<byte[], CancellationToken, Task<string?>> CreateTranscriptionDelegate(
        ModelManagerService models,
        ISettingsService settings
    )
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        return (wav, cancellationToken) =>
            TranscribeAsync(models, settings, wav, cancellationToken);
    }

    private static async Task<string?> TranscribeAsync(
        ModelManagerService models,
        ISettingsService settings,
        byte[] wav,
        CancellationToken cancellationToken
    )
    {
        var effectiveModelId = settings.Current.SelectedModelId;
        await using var lease = await models.AcquireTranscriptionAsync(
            effectiveModelId,
            cancellationToken: cancellationToken
        );
        try
        {
            var languageHints = settings.Current.GetLanguageHints();
            var languageSelection = LanguageSelectionResolver.ResolvePrimary(languageHints);
            var result = await lease.Plugin.TranscribeAsync(
                wav,
                languageSelection,
                languageHints,
                false,
                null,
                cancellationToken
            );
            return result.Text;
        }
        finally
        {
            // Release the model lock before writing to disk so a concurrent
            // dictation isn't blocked by the file I/O that follows.
            // The using-statement above will call DisposeAsync again on
            // exit, but the lease is idempotent so the double-dispose is safe.
            // ReSharper disable once DisposeOnUsingVariable -- intentional early release of the model lock before the file I/O below.
            await lease.DisposeAsync();
        }
    }

    [RelayCommand]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }

        _audioPlayback.Stop();
        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }

            var txtPath = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txtPath))
            {
                File.Delete(txtPath);
            }
        }
        catch
        {
            // Best effort.
        }

        Recordings.Remove(item);
        OnPropertyChanged(nameof(HasRecordings));
    }

    private void LoadExistingRecordings()
    {
        try
        {
            if (!Directory.Exists(_recordingDirectory))
            {
                return;
            }

            foreach (
                var file in Directory
                    .GetFiles(_recordingDirectory, "recording-*.wav")
                    .OrderByDescending(path => path)
            )
            {
                var info = new FileInfo(file);
                var txtFile = Path.ChangeExtension(file, ".txt");
                var transcript = File.Exists(txtFile) ? File.ReadAllText(txtFile) : null;
                Recordings.Add(
                    new RecordingItem(info.Name, file, info.CreationTime, TimeSpan.Zero, transcript)
                );
            }

            OnPropertyChanged(nameof(HasRecordings));
        }
        catch
        {
            // Ignore broken files.
        }
    }
}
