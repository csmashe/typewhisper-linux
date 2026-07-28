using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecorderFileNamerTests : IDisposable
{
    private static readonly DateTime s_timestamp = new(2026, 7, 15, 13, 42, 7);

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.RecorderFileNamerTests"
    );

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_tempDir);
    }

    [Fact]
    public void CommitRecording_WhenStemIsFree_UsesBaseStemAndWritesCompleteBytes()
    {
        byte[] wav = [0, 1, 2, 255];

        var committedPath = RecorderFileNamer.CommitRecording(_tempDir, s_timestamp, wav);

        Assert.Equal(Path.Join(_tempDir, "recording-2026-07-15-134207.wav"), committedPath);
        Assert.Equal(wav, File.ReadAllBytes(committedPath));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public async Task CommitRecording_InParallelAtSameTimestamp_CreatesUniqueMatchingFiles()
    {
        byte[][] recordings = [[1, 2, 3], [4, 5, 6]];
        using var start = new Barrier(recordings.Length);
        var commits = recordings
            .Select(
                wav =>
                    Task.Run(() =>
                    {
                        // ReSharper disable once AccessToDisposedClosure -- Task.WhenAll below
                        // awaits every task before `using var start` is disposed at scope end.
                        start.SignalAndWait();
                        return RecorderFileNamer.CommitRecording(
                            _tempDir,
                            s_timestamp,
                            wav
                        );
                    })
            )
            .ToArray();

        var committedPaths = await Task.WhenAll(commits);

        Assert.Equal(recordings.Length, committedPaths.Distinct().Count());
        Assert.Equal(
            [
                Path.Join(_tempDir, "recording-2026-07-15-134207 (1).wav"),
                Path.Join(_tempDir, "recording-2026-07-15-134207.wav"),
            ],
            committedPaths.Order().ToArray()
        );
        for (var index = 0; index < recordings.Length; index++)
        {
            Assert.Equal(recordings[index], await File.ReadAllBytesAsync(committedPaths[index]));
        }

        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public void CommitRecording_WhenBaseWavExists_AdvancesToNextSuffix()
    {
        var existingPath = Path.Join(_tempDir, "recording-2026-07-15-134207.wav");
        File.WriteAllBytes(existingPath, [9, 8, 7]);
        var existingBytes = File.ReadAllBytes(existingPath);

        var committedPath = RecorderFileNamer.CommitRecording(
            _tempDir,
            s_timestamp,
            [1, 2, 3]
        );

        Assert.Equal(Path.Join(_tempDir, "recording-2026-07-15-134207 (1).wav"), committedPath);
        Assert.Equal(existingBytes, File.ReadAllBytes(existingPath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(committedPath));
    }

    [Fact]
    public void CommitRecording_WhenBaseTranscriptExists_PreservesItAndAdvancesSuffix()
    {
        var transcriptPath = Path.Join(_tempDir, "recording-2026-07-15-134207.txt");
        File.WriteAllBytes(transcriptPath, [0, 1, 2, 255]);
        var transcriptBytes = File.ReadAllBytes(transcriptPath);

        var committedPath = RecorderFileNamer.CommitRecording(
            _tempDir,
            s_timestamp,
            [4, 5, 6]
        );

        Assert.Equal(Path.Join(_tempDir, "recording-2026-07-15-134207 (1).wav"), committedPath);
        Assert.Equal(transcriptBytes, File.ReadAllBytes(transcriptPath));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(committedPath));
    }

    [Fact]
    public void CommitRecording_WhenDirectoryOccupiesBaseWavName_AdvancesSuffix()
    {
        var occupiedPath = Path.Join(_tempDir, "recording-2026-07-15-134207.wav");
        Directory.CreateDirectory(occupiedPath);

        var committedPath = RecorderFileNamer.CommitRecording(
            _tempDir,
            s_timestamp,
            [7, 8, 9]
        );

        Assert.Equal(Path.Join(_tempDir, "recording-2026-07-15-134207 (1).wav"), committedPath);
        Assert.True(Directory.Exists(occupiedPath));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(committedPath));
    }
}
