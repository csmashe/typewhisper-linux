using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class SessionAudioFileServiceTests
{
    [Fact]
    public void DeleteSessionCaptures_RemovesOnlyDictationWavs()
    {
        Directory.CreateDirectory(TypeWhisper.Core.TypeWhisperEnvironment.AudioPath);

        var dictationFile = Path.Combine(TypeWhisper.Core.TypeWhisperEnvironment.AudioPath, $"dictation-{Guid.NewGuid():N}.wav");
        var otherFile = Path.Combine(TypeWhisper.Core.TypeWhisperEnvironment.AudioPath, $"recording-{Guid.NewGuid():N}.wav");

        File.WriteAllText(dictationFile, "dictation");
        File.WriteAllText(otherFile, "other");

        var sut = new SessionAudioFileService();
        sut.DeleteSessionCaptures();

        Assert.False(File.Exists(dictationFile));
        Assert.True(File.Exists(otherFile));

        File.Delete(otherFile);
    }
}
