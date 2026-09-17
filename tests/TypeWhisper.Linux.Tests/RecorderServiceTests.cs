using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecorderServiceTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateTempDirectory("TypeWhisper.RecorderServiceTests");

    public void Dispose() => TestPaths.DeleteDirectory(_directory);

    internal static ISettingsService Settings()
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(value => value.Current).Returns(AppSettings.Default);
        return settings.Object;
    }

    private static AudioRecordingService Audio() => new(_ => { }, () => 0, () => { });

    [Fact]
    public async Task PauseResume_ExcludesPausedTimeAndConcatenatesSegments()
    {
        using var audio = Audio();
        var clock = new ManualTimeProvider();
        using var recorder = new RecorderService(audio, Settings(), _directory, clock);
        Assert.True(await recorder.StartAsync());
        audio.ProcessAudioBufferForTest([0.1f, -0.1f, 0.2f, -0.2f]);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), recorder.ActiveDuration);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.PauseAsync();
        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.True(audio.IsCaptureReserved);
        Assert.Equal(TimeSpan.FromSeconds(4.0 / 16000), recorder.ActiveDuration);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(4.0 / 16000), recorder.ActiveDuration);
        await recorder.ResumeAsync();
        audio.ProcessAudioBufferForTest([0.3f, -0.3f, 0.4f, -0.4f]);
        var result = await recorder.StopAsync();
        Assert.NotNull(result);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(_directory, Path.GetDirectoryName(result.FilePath));
        Assert.Equal(8, RecorderWavSegments.SampleCount(result.Wav));
        Assert.Equal(TimeSpan.FromSeconds(8.0 / 16000), result.Duration);
        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.False(audio.IsCaptureReserved);
        Assert.True(recorder.TryGetSession(result.SessionId.ToUpperInvariant(), out var snapshot));
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal(result.FilePath, snapshot.OutputFile);
    }

    [Fact]
    public async Task StopFromPaused_WritesCombinedFile()
    {
        using var audio = Audio();
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await recorder.StartAsync();
        audio.ProcessAudioBufferForTest([0.1f, -0.1f]);
        await recorder.PauseAsync();
        var result = await recorder.StopAsync();
        Assert.NotNull(result);
        Assert.Equal(2, RecorderWavSegments.SampleCount(await File.ReadAllBytesAsync(result.FilePath)));
    }

    [Fact]
    public async Task StopWithoutAudio_ReturnsNullAndReleasesReservation()
    {
        using var audio = Audio();
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await recorder.StartAsync();
        Assert.Null(await recorder.StopAsync());
        Assert.False(audio.IsCaptureReserved);
        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.True(recorder.TryGetSession(recorder.ActiveSessionId!, out var snapshot));
        Assert.Equal("failed", snapshot.Status);
        Assert.Equal("No audio captured", snapshot.Error);
    }

    [Fact]
    public async Task ResumeFailure_KeepsPausedStateAndReservation()
    {
        var opens = 0;
        using var audio = new AudioRecordingService(
            _ => { if (++opens == 2) { throw new IOException("device unavailable"); } },
            () => 0,
            () => { }
        );
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await recorder.StartAsync();
        audio.ProcessAudioBufferForTest([0.1f]);
        await recorder.PauseAsync();
        await Assert.ThrowsAsync<IOException>(recorder.ResumeAsync);
        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.True(audio.IsCaptureReserved);
        Assert.NotNull(await recorder.StopAsync());
    }

    [Fact]
    public async Task SessionTableKeepsAtMost100Entries()
    {
        using var audio = Audio();
        using var recorder = new RecorderService(audio, Settings(), _directory);
        var ids = new List<string>();
        for (var i = 0; i < 102; i++)
        {
            await recorder.StartAsync();
            ids.Add(recorder.ActiveSessionId!);
            await recorder.StopAsync();
        }

        Assert.Equal(100, ids.Count(id => recorder.TryGetSession(id, out _)));
        Assert.False(recorder.TryGetSession(ids[0], out _));
        Assert.False(recorder.TryGetSession(ids[1], out _));
        Assert.True(recorder.TryGetSession(ids[^1], out _));
    }

    [Fact]
    public async Task SaveFailure_ReleasesReservationAndPublishesSafeError()
    {
        var blocked = Path.Join(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "blocked");
        using var audio = Audio();
        using var recorder = new RecorderService(audio, Settings(), blocked);
        await recorder.StartAsync();
        audio.ProcessAudioBufferForTest([0.1f]);
        await Assert.ThrowsAnyAsync<IOException>(() => recorder.StopAsync());
        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.False(audio.IsCaptureReserved);
        Assert.True(recorder.TryGetSession(recorder.ActiveSessionId!, out var snapshot));
        Assert.Equal("Could not save recording", snapshot.Error);
        Assert.Equal("failed", snapshot.Status);
    }

    [Fact]
    public async Task StopFinalizationFailure_ReleasesReservationAndFailsSession()
    {
        var stops = 0;
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { if (++stops == 1) { throw new IOException("stream stuck"); } }
        );
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await recorder.StartAsync();
        audio.ProcessAudioBufferForTest([0.1f]);
        var id = recorder.ActiveSessionId!;

        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());

        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.False(audio.IsCaptureReserved);
        Assert.True(recorder.TryGetSession(id, out var snapshot));
        Assert.Equal("failed", snapshot.Status);
        Assert.Equal("Could not save recording", snapshot.Error);

        // A stop that failed while tearing the stream down must not strand the
        // recorder in Recording, or dictation stays blocked for the whole session.
        Assert.True(await recorder.StartAsync());
        audio.ProcessAudioBufferForTest([0.2f]);
        Assert.NotNull(await recorder.StopAsync());
    }

    [Fact]
    public async Task StartWhileAnotherTransitionIsInFlight_ThrowsRecorderBusy()
    {
        using var audio = Audio();
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await recorder.StartAsync();
        audio.ProcessAudioBufferForTest([0.1f]);

        var stop = recorder.StopAsync();
        await Assert.ThrowsAsync<RecorderBusyException>(recorder.StartAsync);

        Assert.NotNull(await stop);
    }

    [Fact]
    public async Task StartFailure_DoesNotCreateSessionOrRetainReservation()
    {
        using var audio = new AudioRecordingService(_ => throw new IOException("busy"), () => 0, () => { });
        using var recorder = new RecorderService(audio, Settings(), _directory);
        await Assert.ThrowsAsync<IOException>(recorder.StartAsync);
        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.Null(recorder.ActiveSessionId);
        Assert.False(audio.IsCaptureReserved);
    }

    [Fact]
    public async Task Dispose_StopsActiveCaptureAndReleasesReservation()
    {
        using var audio = Audio();
        var recorder = new RecorderService(audio, Settings(), _directory);
        try
        {
            Assert.True(await recorder.StartAsync());
            Assert.True(audio.IsRecording);
        }
        finally
        {
            recorder.Dispose();
        }

        Assert.False(audio.IsRecording);
        Assert.False(audio.IsCaptureReserved);
        var session = audio.TryStartRecording(false);
        Assert.NotNull(session);
        audio.StopRecording(session);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += (long)(elapsed.TotalSeconds * TimestampFrequency);
    }
}
