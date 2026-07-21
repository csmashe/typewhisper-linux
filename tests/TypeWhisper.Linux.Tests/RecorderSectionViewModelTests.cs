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
            async _ =>
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
            _ =>
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
            _ =>
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
    public async Task ToggleRecordingCommand_DoneRequiresDurableTranscript()
    {
        const string expectedTranscript = "The full durable transcript.\nSecond line.";
        using var audio = CreateAudioService();
        var sut = CreateViewModel(
            audio,
            _tempDir,
            _ => Task.FromResult<string?>(expectedTranscript)
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
        var sut = CreateViewModel(audio, _tempDir, _ => Task.FromResult(transcript));
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

    private static AudioRecordingService CreateAudioService()
    {
        return new AudioRecordingService(_ => { }, () => 0, () => { });
    }

    private static RecorderSectionViewModel CreateViewModel(
        AudioRecordingService audio,
        string recordingDirectory,
        Func<byte[], Task<string?>> transcribeAsync
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

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            var updated = mutate(Current);
            Save(updated);
            return updated;
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
