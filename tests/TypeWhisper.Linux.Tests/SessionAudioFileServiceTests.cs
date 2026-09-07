using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SessionAudioFileServiceTests : IDisposable
{
    private readonly string _audioDirectory = TestPaths.CreateTempDirectory(
        "TypeWhisper.SessionAudioFileServiceTests"
    );

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_audioDirectory);
    }

    [Fact]
    public void DeleteSessionCaptures_RemovesOnlyDictationWavs()
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

        service.DeleteSessionCaptures();

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
