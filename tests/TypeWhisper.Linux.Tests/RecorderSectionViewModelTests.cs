using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecorderSectionViewModelTests : IDisposable
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(5);
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.RecorderSectionViewModelTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public async Task ToggleRecordingCommand_AwaitsAndSerializesCompleteWorkflow()
    {
        const string expectedTranscript = "A complete gated transcript.";
        var transcriptionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseTranscription = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            async (_, _) =>
            {
                transcriptionStarted.SetResult();
                await releaseTranscription.Task;
                return expectedTranscript;
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        var stopTask = sut.ToggleRecordingCommand.ExecuteAsync(null);
        await transcriptionStarted.Task;

        try
        {
            Assert.False(stopTask.IsCompleted);
            Assert.True(sut.ToggleRecordingCommand.IsRunning);
            Assert.False(sut.ToggleRecordingCommand.CanExecute(null));
            Assert.True(sut.IsTranscribing);
            Assert.Equal(
                Loc.Instance["Recorder.StatusSavedTranscribing"],
                sut.StatusText
            );
            Assert.Single(Directory.GetFiles(_tempDir, "recording-*.wav"));
        }
        finally
        {
            releaseTranscription.TrySetResult();
            await stopTask;
        }

        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Single(sut.Recordings);
        Assert.Equal(Loc.Instance["Recorder.StatusDone"], sut.StatusText);
    }

    [Fact]
    public async Task QuiesceAsync_CancelsTranscriptionAndAwaitsWorkflow()
    {
        var transcriptionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseTranscription = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            async (_, cancellationToken) =>
            {
                transcriptionStarted.TrySetResult();
                await using var registration = cancellationToken.Register(
                    cancellationObserved.SetResult
                );
                await releaseTranscription.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return "shutdown should sacrifice this transcript";
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        var stopTask = sut.ToggleRecordingCommand.ExecuteAsync(null);
        await transcriptionStarted.Task.WaitAsync(s_testGuard);
        Task<bool> quiesceTask;

        try
        {
            Assert.Single(Directory.GetFiles(_tempDir, "recording-*.wav"));

            quiesceTask = sut.QuiesceAsync(s_testGuard);
            await cancellationObserved.Task.WaitAsync(s_testGuard);

            Assert.False(quiesceTask.IsCompleted);
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            releaseTranscription.TrySetResult();
        }

        Assert.True(await quiesceTask.WaitAsync(s_testGuard));
        await stopTask.WaitAsync(s_testGuard);
        var recording = Assert.Single(sut.Recordings);
        Assert.True(File.Exists(recording.FilePath));
        Assert.False(File.Exists(Path.ChangeExtension(recording.FilePath, ".txt")));
        Assert.Null(recording.Transcript);
    }

    [Fact]
    public async Task QuiesceAsync_ClosesCommandIngress()
    {
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) => Task.FromResult<string?>(null)
        );

        Assert.True(await sut.QuiesceAsync(s_testGuard).WaitAsync(s_testGuard));

        await sut.ToggleRecordingCommand.ExecuteAsync(null).WaitAsync(s_testGuard);

        Assert.False(sut.IsRecording);
        Assert.Equal(Loc.Instance["Recorder.StatusReady"], sut.StatusText);
        Assert.Empty(Directory.GetFiles(_tempDir, "recording-*.wav"));
    }

    [Fact]
    public async Task QuiesceAsync_WhenTranscriberIgnoresCancellation_TimesOut()
    {
        var transcriptionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseTranscription = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            async (_, _) =>
            {
                transcriptionStarted.TrySetResult();
                await releaseTranscription.Task;
                return null;
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        var stopTask = sut.ToggleRecordingCommand.ExecuteAsync(null);
        await transcriptionStarted.Task.WaitAsync(s_testGuard);
        Task<bool>? quiesceTask = null;

        try
        {
            quiesceTask = sut.QuiesceAsync(TimeSpan.FromMilliseconds(25));

            Assert.False(await quiesceTask.WaitAsync(s_testGuard));
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            releaseTranscription.TrySetResult();
            await stopTask.WaitAsync(s_testGuard);
            if (quiesceTask is not null)
            {
                await quiesceTask.WaitAsync(s_testGuard);
            }
        }
    }

    [Fact]
    public async Task TearDownAsync_CancelsAndAwaitsRecorderBeforeAudioDispose()
    {
        var transcriptionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseTranscription = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            async (_, cancellationToken) =>
            {
                transcriptionStarted.TrySetResult();
                await using var registration = cancellationToken.Register(
                    cancellationObserved.SetResult
                );
                await releaseTranscription.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return "shutdown should sacrifice this transcript";
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        var stopTask = sut.ToggleRecordingCommand.ExecuteAsync(null);
        await transcriptionStarted.Task.WaitAsync(s_testGuard);
        Task? tearDownTask = null;

        try
        {
            tearDownTask = App.TearDownAsync(new TearDownServiceProvider(sut, audio));
            await cancellationObserved.Task.WaitAsync(s_testGuard);

            Assert.False(tearDownTask.IsCompleted);
            var probeSession = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
                audio.TryStartRecording(whisperModeEnabled: false)
            );
            audio.ProcessAudioBufferForTest([0.25f, -0.25f]);
            // ReSharper disable once MethodHasAsyncOverload -- the synchronous probe avoids adding an unrelated tail-drain workflow while teardown is parked.
            Assert.True(audio.StopRecording(probeSession).Length > 44);
            Assert.False(tearDownTask.IsCompleted);
        }
        finally
        {
            releaseTranscription.TrySetResult();
            await stopTask.WaitAsync(s_testGuard);
            if (tearDownTask is not null)
            {
                await tearDownTask.WaitAsync(s_testGuard);
            }

            App.ResetShutdownDisposalDecisionForTests();
        }

        Assert.Null(audio.TryStartRecording(whisperModeEnabled: false));
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenAudioAlreadyOwned_ShowsNoMicrophoneAndDoesNotClaimRecording()
    {
        using var audio = CreateAudioService();
        var foreignSession = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            audio.TryStartRecording(whisperModeEnabled: false)
        );
        var sut = CreateViewModel(audio, _tempDir, (_, _) => Task.FromResult<string?>(null));

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Instance["Recorder.StatusNoMicrophone"], sut.StatusText);
        Assert.False(sut.IsRecording);
        Assert.Equal(Loc.Instance["Recorder.Record"], sut.RecordButtonText);
        Assert.True(audio.IsRecordingOwnedBy(foreignSession));

        audio.ProcessAudioBufferForTest([0.1f, -0.1f]);
        // ReSharper disable once MethodHasAsyncOverload -- synchronous teardown keeps this focused on ownership; StopRecordingAsync would add its 120ms drain for nothing.
        Assert.True(audio.StopRecording(foreignSession).Length > 44);
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenCaptureSessionBecomesStale_ShowsNoAudioAndResetsState()
    {
        var transcriptionInvocations = 0;
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) =>
            {
                transcriptionInvocations++;
                return Task.FromResult<string?>("must not be returned");
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);
        // ReSharper disable once DisposeOnUsingVariable -- disposing mid-recording is what makes the capture session stale, the behavior under test; the using re-dispose at scope end is idempotent.
        audio.Dispose();

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Instance["Recorder.StatusNoAudio"], sut.StatusText);
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Equal(0, sut.AudioLevel);
        Assert.Equal("0:00", sut.DurationText);
        Assert.Equal(Loc.Instance["Recorder.Record"], sut.RecordButtonText);
        Assert.Empty(Directory.GetFiles(_tempDir, "recording-*.wav"));
        Assert.Equal(0, transcriptionInvocations);
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenTranscriptionThrows_KeepsWavAndReportsSavedNoModel()
    {
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) => Task.FromException<string?>(new InvalidOperationException("model failed"))
        );
        await StartRecordingWithFramesAsync(sut, audio);

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        var recording = Assert.Single(sut.Recordings);
        Assert.True(File.Exists(recording.FilePath));
        Assert.False(File.Exists(Path.ChangeExtension(recording.FilePath, ".txt")));
        Assert.Null(recording.Transcript);
        Assert.Equal(Loc.Instance["Recorder.StatusSavedNoModel"], sut.StatusText);
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenWavCommitFails_ShowsSaveFailureAndRecovers()
    {
        var poisonedParent = Path.Join(_tempDir, "poisoned-parent");
        await File.WriteAllTextAsync(poisonedParent, "not a directory");
        var poisonedRecordingDirectory = Path.Join(poisonedParent, "recordings");
        var transcriptionInvocations = 0;
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            poisonedRecordingDirectory,
            (_, _) =>
            {
                transcriptionInvocations++;
                return Task.FromResult<string?>("must not be returned");
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        var exception = await Record.ExceptionAsync(
            () => sut.ToggleRecordingCommand.ExecuteAsync(null)
        );

        Assert.Null(exception);
        Assert.Equal(Loc.Instance["Recorder.StatusSaveFailed"], sut.StatusText);
        Assert.Empty(sut.Recordings);
        Assert.False(sut.HasRecordings);
        Assert.Equal(0, transcriptionInvocations);
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Equal(0, sut.AudioLevel);
        Assert.Equal("0:00", sut.DurationText);
        Assert.Equal(Loc.Instance["Recorder.Record"], sut.RecordButtonText);
        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenTranscriptCommitFails_KeepsWavAndTranscript()
    {
        const string expectedTranscript = "Keep this transcript available for copying.";
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) =>
            {
                var wavPath = Directory.GetFiles(_tempDir, "recording-*.wav").Single();
                Directory.CreateDirectory(Path.ChangeExtension(wavPath, ".txt"));
                return Task.FromResult<string?>(expectedTranscript);
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        var recording = Assert.Single(sut.Recordings);
        var transcriptPath = Path.ChangeExtension(recording.FilePath, ".txt");
        Assert.True(File.Exists(recording.FilePath));
        Assert.Equal(expectedTranscript, recording.Transcript);
        Assert.False(File.Exists(transcriptPath));
        Assert.True(Directory.Exists(transcriptPath));
        Assert.Equal(
            Loc.Instance["Recorder.StatusTranscriptSaveFailed"],
            sut.StatusText
        );
        Assert.NotEqual(Loc.Instance["Recorder.StatusDone"], sut.StatusText);
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Equal("0:00", sut.DurationText);
        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleRecordingCommand_WhenTranscriptPathIsClaimedAfterWavCommit_PreservesForeignFile()
    {
        const string expectedTranscript = "Keep this transcript in the recording list.";
        byte[] foreignBytes = [0, 1, 2, 255, 3, 4];
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) =>
            {
                var wavPath = Directory.GetFiles(_tempDir, "recording-*.wav").Single();
                File.WriteAllBytes(Path.ChangeExtension(wavPath, ".txt"), foreignBytes);
                return Task.FromResult<string?>(expectedTranscript);
            }
        );
        await StartRecordingWithFramesAsync(sut, audio);

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        var recording = Assert.Single(sut.Recordings);
        var transcriptPath = Path.ChangeExtension(recording.FilePath, ".txt");
        Assert.True(File.Exists(recording.FilePath));
        Assert.Equal(expectedTranscript, recording.Transcript);
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(transcriptPath));
        Assert.Equal(
            Loc.Instance["Recorder.StatusTranscriptSaveFailed"],
            sut.StatusText
        );
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Equal("0:00", sut.DurationText);
        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleRecordingCommand_DoneRequiresDurableTranscript()
    {
        const string expectedTranscript = "The full durable transcript.\nSecond line.";
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            (_, _) => Task.FromResult<string?>(expectedTranscript)
        );
        await StartRecordingWithFramesAsync(sut, audio);

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        var recording = Assert.Single(sut.Recordings);
        var transcriptPath = Path.ChangeExtension(recording.FilePath, ".txt");
        Assert.True(File.Exists(recording.FilePath));
        Assert.Equal(expectedTranscript, recording.Transcript);
        Assert.True(File.Exists(transcriptPath));
        Assert.Equal(expectedTranscript, await File.ReadAllTextAsync(transcriptPath));
        Assert.Equal(Loc.Instance["Recorder.StatusDone"], sut.StatusText);
        Assert.False(sut.IsRecording);
        Assert.False(sut.IsTranscribing);
        Assert.Equal("0:00", sut.DurationText);
        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ToggleRecordingCommand_WhenTranscriptBlankOrMissing_ReportsSavedNoModelWithoutSidecar(
        string? transcript
    )
    {
        using var audio = CreateAudioService();
        var sut = CreateViewModel(audio, _tempDir, (_, _) => Task.FromResult(transcript));
        await StartRecordingWithFramesAsync(sut, audio);

        await sut.ToggleRecordingCommand.ExecuteAsync(null);

        var recording = Assert.Single(sut.Recordings);
        var transcriptPath = Path.ChangeExtension(recording.FilePath, ".txt");
        Assert.True(File.Exists(recording.FilePath));
        Assert.False(File.Exists(transcriptPath));
        Assert.Equal(Loc.Instance["Recorder.StatusSavedNoModel"], sut.StatusText);
        Assert.NotEqual(Loc.Instance["Recorder.StatusDone"], sut.StatusText);
        Assert.False(sut.IsTranscribing);
        Assert.Equal("0:00", sut.DurationText);
        Assert.False(sut.ToggleRecordingCommand.IsRunning);
        Assert.True(sut.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AudioLevel_ServiceDeliveryIsDirectAndStoppedRecorderRejectsLaterDelivery()
    {
        var timeProvider = new ManualTimeProvider();
        var serviceJobs = new List<Action>();
        using var audio = CreateAudioService(serviceJobs.Add, timeProvider);
        var sut = CreateViewModel(audio, _tempDir, (_, _) => Task.FromResult<string?>(null));
        await sut.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(sut.IsRecording);

        audio.ProcessAudioBufferForTest([0.1f]);
        var serviceDelivery = Assert.Single(serviceJobs);

        serviceDelivery();

        Assert.Equal(0.8, sut.AudioLevel, precision: 5);
        sut.IsRecording = false;
        sut.AudioLevel = 0;

        timeProvider.Advance(TimeSpan.FromMilliseconds(66));
        audio.ProcessAudioBufferForTest([0.2f]);
        serviceJobs[1]();

        Assert.Equal(0, sut.AudioLevel);

        sut.IsRecording = true;
        await sut.ToggleRecordingCommand.ExecuteAsync(null);
    }

    private static AudioRecordingService CreateAudioService(
        Action<Action>? postToUiThread = null,
        TimeProvider? timeProvider = null
    )
    {
        return new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            timeProvider: timeProvider,
            postToUiThread: postToUiThread
        );
    }

    [Fact]
    public async Task QuiesceAsync_TreatsFaultedWorkflowAsDrained()
    {
        using var audio = new AudioRecordingService(
            _ => throw new InvalidOperationException("device busy"),
            () => 0,
            () => { }
        );
        var sut = CreateViewModel(audio, _tempDir, (_, _) => Task.FromResult<string?>(null));

        // A busy/unopenable microphone makes TryStartRecording rethrow, leaving the
        // published workflow faulted for the rest of the view-model's lifetime.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ToggleRecordingCommand.ExecuteAsync(null)
        );

        // A settled-faulted workflow is a quiet recording lane: it must classify as
        // drained, not force shutdown onto the skip-all-disposal path.
        Assert.True(await sut.QuiesceAsync(TimeSpan.FromSeconds(5)));
    }

    private static RecorderSectionViewModel CreateViewModel(
        AudioRecordingService audio,
        string recordingDirectory,
        Func<byte[], CancellationToken, Task<string?>> transcribeAsync
    )
    {
        return new RecorderSectionViewModel(
            audio,
            new FakeSettingsService(AppSettings.Default),
            recordingDirectory,
            transcribeAsync
        );
    }

    private static async Task StartRecordingWithFramesAsync(
        RecorderSectionViewModel sut,
        AudioRecordingService audio
    )
    {
        await sut.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(sut.IsRecording);
        Assert.Equal(Loc.Instance["Recorder.Stop"], sut.RecordButtonText);
        audio.ProcessAudioBufferForTest([0.1f, -0.1f, 0.2f, -0.2f]);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += (long)(elapsed.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class TearDownServiceProvider(
        RecorderSectionViewModel recorder,
        AudioRecordingService audio
    ) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(RecorderSectionViewModel))
            {
                return recorder;
            }

            return serviceType == typeof(AudioRecordingService) ? audio : null;
        }
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        // ISettingsService.Update must read and persist under the same gate as Save.
        private readonly Lock _gate = new();

        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            lock (_gate)
            {
                Current = settings;
                SettingsChanged?.Invoke(settings);
            }
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            lock (_gate)
            {
                var updated = mutate(Current);
                Save(updated);
                return updated;
            }
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
