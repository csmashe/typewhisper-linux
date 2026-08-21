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

public sealed record RecordingItem(
    string FileName,
    string FilePath,
    DateTime CreatedAt,
    TimeSpan Duration,
    string? Transcript
);

public partial class RecorderSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly string _recordingDirectory;
    private readonly ISettingsService _settings;
    private readonly Func<byte[], CancellationToken, Task<string?>> _transcribeAsync;
    private readonly CancellationTokenSource _transcriptionCancellation = new();
    private readonly Lock _workflowGate = new();
    private AudioRecordingService.AudioCaptureSession? _captureSession;
    private bool _commandIngressClosed;
    private Task _publishedWorkflow = Task.CompletedTask;

    [ObservableProperty]
    private double _audioLevel;

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTranscribing;

    private DateTime _recordingStart;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Recorder.StatusReady"];

    private Timer? _timer;

    public RecorderSectionViewModel(
        AudioRecordingService audio,
        ModelManagerService models,
        ISettingsService settings
    )
        : this(
            audio,
            settings,
            TypeWhisperEnvironment.AudioPath,
            CreateTranscriptionDelegate(models, settings)
        )
    {
    }

    internal RecorderSectionViewModel(
        AudioRecordingService audio,
        ModelManagerService models,
        ISettingsService settings,
        string recordingDirectory
    )
        : this(
            audio,
            settings,
            recordingDirectory,
            CreateTranscriptionDelegate(models, settings)
        )
    {
    }

    internal RecorderSectionViewModel(
        AudioRecordingService audio,
        ISettingsService settings,
        string recordingDirectory,
        Func<byte[], CancellationToken, Task<string?>> transcribeAsync
    )
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingDirectory);
        ArgumentNullException.ThrowIfNull(transcribeAsync);

        _audio = audio;
        _settings = settings;
        _recordingDirectory = recordingDirectory;
        _transcribeAsync = transcribeAsync;
        _audio.LevelChanged += (_, level) =>
        {
            if (IsRecording)
            {
                AudioLevel = Math.Clamp(level * 8, 0, 1);
            }
        };
        LoadExistingRecordings();
    }

    public string RecordButtonText =>
        IsRecording ? Loc.Instance["Recorder.Stop"] : Loc.Instance["Recorder.Record"];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    [RelayCommand]
    private Task ToggleRecording()
    {
        TaskCompletionSource publishedWorkflow;
        Task workflow;
        bool stopRecording;
        lock (_workflowGate)
        {
            if (_commandIngressClosed)
            {
                return Task.CompletedTask;
            }

            publishedWorkflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
            workflow = publishedWorkflow.Task;
            stopRecording = IsRecording;
            _publishedWorkflow = workflow;
        }

        _ = RunToggleRecordingWorkflowAsync(
            publishedWorkflow,
            stopRecording,
            _transcriptionCancellation.Token
        );
        return workflow;
    }

    private async Task RunToggleRecordingWorkflowAsync(
        TaskCompletionSource publishedWorkflow,
        bool stopRecording,
        CancellationToken transcriptionCancellationToken
    )
    {
        try
        {
            if (stopRecording)
            {
                await StopRecordingAsync(transcriptionCancellationToken);
            }
            else
            {
                StartRecording();
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
            workflow = _publishedWorkflow;
        }

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

    private void StartRecording()
    {
        var captureSession = _audio.TryStartRecording(_settings.Current.WhisperModeEnabled);
        if (captureSession is null)
        {
            StatusText = Loc.Instance["Recorder.StatusNoMicrophone"];
            return;
        }

        _captureSession = captureSession;
        IsRecording = true;
        OnPropertyChanged(nameof(RecordButtonText));
        _recordingStart = DateTime.UtcNow;
        StatusText = Loc.Instance["Recorder.StatusRecording"];

        _timer = new Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _recordingStart;
            Dispatcher.UIThread.Post(() =>
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}"
            );
        };
        _timer.Start();
    }

    private async Task StopRecordingAsync(CancellationToken transcriptionCancellationToken)
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;

        var duration = DateTime.UtcNow - _recordingStart;
        var captureSession = _captureSession;
        _captureSession = null;
        byte[] wav;
        string filePath;

        try
        {
            wav = captureSession is null
                ? []
                : await _audio.StopRecordingAsync(captureSession, CancellationToken.None);
            if (wav.Length == 0)
            {
                StatusText = Loc.Instance["Recorder.StatusNoAudio"];
                DurationText = "0:00";
                return;
            }

            // Off the dispatcher so a large WAV or slow disk doesn't freeze the UI;
            // CommitRecording touches no UI state.
            var wavBytes = wav;
            filePath = await Task.Run(
                () => RecorderFileNamer.CommitRecording(_recordingDirectory, DateTime.Now, wavBytes),
                CancellationToken.None
            );
        }
        catch
        {
            StatusText = Loc.Instance["Recorder.StatusSaveFailed"];
            DurationText = "0:00";
            return;
        }
        finally
        {
            IsRecording = false;
            OnPropertyChanged(nameof(RecordButtonText));
            AudioLevel = 0;
        }

        var fileName = Path.GetFileName(filePath);

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
        if (!string.IsNullOrWhiteSpace(transcript))
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
        Recordings.Insert(
            0,
            new RecordingItem(fileName, filePath, DateTime.Now, duration, transcript)
        );
        OnPropertyChanged(nameof(HasRecordings));
        StatusText = transcriptWriteFailed
            ? Loc.Instance["Recorder.StatusTranscriptSaveFailed"]
            : transcriptionException is not null
                ? Loc.Instance.GetString(
                    "Recorder.StatusSavedTranscriptionFailed",
                    transcriptionException.Message
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
            var result = await lease.Plugin.TranscribeAsync(
                wav,
                LanguageSelectionResolver.Resolve(settings.Current.Language),
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
