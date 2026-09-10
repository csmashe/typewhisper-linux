using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SessionAudioFileServiceTests : IDisposable
{
    [Fact]
    public void ApplyRetention_WithoutHistory_KeepsFreshCapture()
    {
        var service = new SessionAudioFileService(_audioDirectory);
        var path = service.SaveDictationCapture([1, 2, 3]);
        service.ApplyRetention(30);
        Assert.True(File.Exists(path));
    }

    private readonly string _audioDirectory = TestPaths.CreateTempDirectory(
        "TypeWhisper.SessionAudioFileServiceTests"
    );

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_audioDirectory);
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(40, true, false)]
    [InlineData(2, false, false)]
    [InlineData(40, false, false)]
    public void ApplyRetention_PrunesExpiredAndOrphanedCaptures(int age, bool referenced, bool kept)
    {
        var history = new HistoryService(Path.Join(_audioDirectory, "history.json"), _audioDirectory);
        var service = new SessionAudioFileService(_audioDirectory, history);
        var path = service.SaveDictationCapture([1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        if (referenced)
            history.AddRecord(new TranscriptionRecord
            {
                Id = "test", Timestamp = DateTime.UtcNow, RawText = "", FinalText = "",
                AudioFileName = Path.GetFileName(path), Status = TranscriptionRecordStatus.TranscriptionFailed,
            });
        service.ApplyRetention(30);
        Assert.Equal(kept, File.Exists(path));
        if (!kept)
            return;

        service.ApplyRetention(-1);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ApplyRetention_UnreadableHistory_KeepsCaptures_ThenReadableEmptyHistoryPrunesOrphans()
    {
        var historyPath = Path.Join(_audioDirectory, "history.json");
        File.WriteAllText(historyPath, "[]");
        var history = new HistoryService(historyPath, _audioDirectory);
        var service = new SessionAudioFileService(_audioDirectory, history);
        var first = service.SaveDictationCapture([1]);
        var second = service.SaveDictationCapture([2]);

        using (new FileStream(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            service.ApplyRetention(30);

            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }

        service.ApplyRetention(30);

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void AudioPath_RejectsTraversalAndSymbolicLinks()
    {
        var service = new SessionAudioFileService(_audioDirectory);
        var path = service.SaveDictationCapture([1]);
        Assert.Null(service.GetAudioPath("../" + Path.GetFileName(path)));
        Assert.Null(service.GetAudioPath(path));
        File.CreateSymbolicLink(Path.Join(_audioDirectory, "dictation-link.wav"), path);
        Assert.False(service.HasAudio("dictation-link.wav"));
    }

    [Fact]
    public void ApplyRetention_Disabled_RemovesOnlyDictationWavs()
    {
        var service = new SessionAudioFileService(_audioDirectory);

        var dictationFile = Path.Join(
            _audioDirectory,
            $"dictation-{Guid.NewGuid():N}.wav"
        );
        var otherFile = Path.Join(
            _audioDirectory,
            $"recording-{Guid.NewGuid():N}.wav"
        );

        File.WriteAllText(dictationFile, "dictation");
        File.WriteAllText(otherFile, "other");

        service.ApplyRetention(-1);

        Assert.False(File.Exists(dictationFile));
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public void SaveDictationCapture_Throws_WhenAudioDirectoryPathIsBlockedByAFile()
    {
        var blockingFile = Path.Join(_audioDirectory, "not-a-directory");
        File.WriteAllText(blockingFile, "blocked");
        var service = new SessionAudioFileService(Path.Join(blockingFile, "captures"));

        Assert.ThrowsAny<IOException>(() => service.SaveDictationCapture([1, 2, 3]));
    }
}
