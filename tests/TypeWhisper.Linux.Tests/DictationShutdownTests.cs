using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationShutdownTests
{
    [Fact]
    public void AppShutdown_DisposesDictationBeforeAudio()
    {
        var calls = new List<string>();
        var dictation = new RecordingDisposable(calls, "dictation");
        var audio = new RecordingDisposable(calls, "audio");

        App.DisposeDictationBeforeAudio(dictation, audio);

        Assert.Equal(["dictation", "audio"], calls);
        Assert.Equal(1, dictation.DisposeCallCount);
        Assert.Equal(1, audio.DisposeCallCount);
    }

    [Fact]
    public void AppShutdown_WhenDictationDisposeThrows_StillDisposesAudio()
    {
        var calls = new List<string>();
        var dictation = new RecordingDisposable(calls, "dictation", throwOnDispose: true);
        var audio = new RecordingDisposable(calls, "audio");

        var exception = Record.Exception(() =>
            App.DisposeDictationBeforeAudio(dictation, audio)
        );

        Assert.Null(exception);
        Assert.Equal(["dictation", "audio"], calls);
        Assert.Equal(1, dictation.DisposeCallCount);
        Assert.Equal(1, audio.DisposeCallCount);
    }

    [Fact]
    public void AppShutdown_WhenAudioDisposeThrows_ReturnsForLaterTeardown()
    {
        var calls = new List<string>();
        var dictation = new RecordingDisposable(calls, "dictation");
        var audio = new RecordingDisposable(calls, "audio", throwOnDispose: true);

        var exception = Record.Exception(() =>
            App.DisposeDictationBeforeAudio(dictation, audio)
        );

        Assert.Null(exception);
        Assert.Equal(["dictation", "audio"], calls);
        Assert.Equal(1, dictation.DisposeCallCount);
        Assert.Equal(1, audio.DisposeCallCount);
    }

    [Fact]
    public void AudioCleanup_WithMatchingToken_StopsCaptureOnceAndRestoresBothEffects()
    {
        var streamStopCount = 0;
        using var audio = CreateAudioService(() => streamStopCount++);
        var captureSession = StartRecording(audio);
        var ducking = new FakeAudioDuckingService();
        var media = new FakeMediaPauseService();

        DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
            audio,
            captureSession,
            ducking,
            media
        );

        Assert.False(audio.IsRecordingOwnedBy(captureSession));
        Assert.Equal(1, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    [Fact]
    public void AudioCleanup_AfterRecorderWasDisposed_StillRestoresBothEffects()
    {
        var streamStopCount = 0;
        var audio = CreateAudioService(() => streamStopCount++);
        var formerCaptureSession = StartRecording(audio);
        var ducking = new FakeAudioDuckingService();
        var media = new FakeMediaPauseService();
        audio.Dispose();

        DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
            audio,
            formerCaptureSession,
            ducking,
            media
        );

        Assert.Equal(1, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    [Fact]
    public void AudioCleanup_WithStaleToken_DoesNotStopNewOwnerButRestoresBothEffects()
    {
        var streamStopCount = 0;
        using var audio = CreateAudioService(() => streamStopCount++);
        var staleCaptureSession = StartRecording(audio);
        audio.StopRecording(staleCaptureSession);
        var newerCaptureSession = StartRecording(audio);
        var ducking = new FakeAudioDuckingService();
        var media = new FakeMediaPauseService();

        DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
            audio,
            staleCaptureSession,
            ducking,
            media
        );

        Assert.True(audio.IsRecordingOwnedBy(newerCaptureSession));
        Assert.Equal(1, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    [Fact]
    public void AudioCleanup_WithoutToken_StillRestoresBothEffects()
    {
        var streamStopCount = 0;
        using var audio = CreateAudioService(() => streamStopCount++);
        var ducking = new FakeAudioDuckingService();
        var media = new FakeMediaPauseService();

        DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
            audio,
            null,
            ducking,
            media
        );

        Assert.Equal(0, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    [Fact]
    public void AudioCleanup_WhenCaptureStopThrows_StillRestoresBothEffects()
    {
        var streamStopCount = 0;
        using var audio = CreateAudioService(() =>
        {
            streamStopCount++;
            if (streamStopCount == 1)
            {
                throw new InvalidOperationException("Stream stop failed.");
            }
        });
        var captureSession = StartRecording(audio);
        var ducking = new FakeAudioDuckingService();
        var media = new FakeMediaPauseService();

        var exception = Record.Exception(() =>
            DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
                // ReSharper disable once AccessToDisposedClosure -- lambda runs synchronously inside Record.Exception, before the using disposes audio.
                audio,
                captureSession,
                ducking,
                media
            )
        );

        Assert.Null(exception);
        Assert.Equal(1, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    [Fact]
    public void AudioCleanup_WhenRestoreThrows_StillAttemptsMediaResume()
    {
        var streamStopCount = 0;
        using var audio = CreateAudioService(() => streamStopCount++);
        var ducking = new FakeAudioDuckingService(throwOnRestore: true);
        var media = new FakeMediaPauseService();

        var exception = Record.Exception(() =>
            DictationOrchestrator.StopCaptureAndRestoreSystemAudio(
                // ReSharper disable once AccessToDisposedClosure -- lambda runs synchronously inside Record.Exception, before the using disposes audio.
                audio,
                null,
                ducking,
                media
            )
        );

        Assert.Null(exception);
        Assert.Equal(0, streamStopCount);
        Assert.Equal(1, ducking.RestoreCallCount);
        Assert.Equal(1, media.ResumeCallCount);
    }

    private static AudioRecordingService CreateAudioService(Action stopStream)
    {
        return new AudioRecordingService(_ => { }, () => 0, stopStream);
    }

    private static AudioRecordingService.AudioCaptureSession StartRecording(
        AudioRecordingService audio
    )
    {
        return Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            audio.TryStartRecording(whisperModeEnabled: false)
        );
    }

    private sealed class RecordingDisposable(
        ICollection<string> calls,
        string name,
        bool throwOnDispose = false
    ) : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
            calls.Add(name);
            if (throwOnDispose)
            {
                throw new InvalidOperationException($"{name} dispose failed.");
            }
        }
    }

    private sealed class FakeAudioDuckingService(bool throwOnRestore = false)
        : IAudioDuckingService
    {
        public int RestoreCallCount { get; private set; }

        public void DuckAudio(float factor)
        {
            throw new InvalidOperationException("Unexpected duck call.");
        }

        public void RestoreAudio()
        {
            RestoreCallCount++;
            if (throwOnRestore)
            {
                throw new InvalidOperationException("Restore failed.");
            }
        }
    }

    private sealed class FakeMediaPauseService : IMediaPauseService
    {
        public int ResumeCallCount { get; private set; }

        public void PauseMedia()
        {
            throw new InvalidOperationException("Unexpected pause call.");
        }

        public void ResumeMedia()
        {
            ResumeCallCount++;
        }
    }
}
