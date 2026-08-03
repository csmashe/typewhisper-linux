// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read/WriteAllText is deliberate in these test assertions; the async overload would only add await noise with no benefit off the hot path.
using System.Collections.Concurrent;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class WatchFolderServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.WatchFolderServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task Start_WhenSourceBasenamesCollide_CommitsDistinctExportsBeforeDeletingSources()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var wavPath = Path.Join(watchPath, "meeting.wav");
        var mp3Path = Path.Join(watchPath, "meeting.mp3");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        File.WriteAllBytes(mp3Path, [4, 5, 6]);

        using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 2,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.Markdown,
                DeleteSource: true
            )
        );
        service.Stop();

        Assert.All(processed, item => Assert.True(item.Success, item.ErrorMessage));
        Assert.Equal(
            [Path.Join(outputPath, "meeting (1).md"), Path.Join(outputPath, "meeting.md")],
            processed.Select(item => item.OutputPath).Order(StringComparer.Ordinal)
        );
        Assert.All(processed, item => Assert.True(File.Exists(item.OutputPath)));
        Assert.False(File.Exists(wavPath));
        Assert.False(File.Exists(mp3Path));
    }

    [Fact]
    public async Task Start_WhenUserExportsExist_PreservesBytesAndAdvancesSuffix()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "meeting.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        var baseOutputPath = Path.Join(outputPath, "meeting.txt");
        var firstSuffixPath = Path.Join(outputPath, "meeting (1).txt");
        File.WriteAllBytes(baseOutputPath, [0, 1, 2, 255]);
        File.WriteAllBytes(firstSuffixPath, [255, 2, 1, 0]);
        var baseBytes = File.ReadAllBytes(baseOutputPath);
        var firstSuffixBytes = File.ReadAllBytes(firstSuffixPath);

        using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 1,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.PlainText,
                DeleteSource: false
            )
        );
        service.Stop();

        var item = Assert.Single(processed);
        Assert.True(item.Success, item.ErrorMessage);
        Assert.Equal(Path.Join(outputPath, "meeting (2).txt"), item.OutputPath);
        Assert.Equal("Transcribed meeting.wav", File.ReadAllText(item.OutputPath));
        Assert.Equal(baseBytes, File.ReadAllBytes(baseOutputPath));
        Assert.Equal(firstSuffixBytes, File.ReadAllBytes(firstSuffixPath));
    }

    [Fact]
    public async Task Start_WhenExportNameIsOccupiedByDirectory_AdvancesSuffix()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "meeting.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        Directory.CreateDirectory(Path.Join(outputPath, "meeting.txt"));

        using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 1,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.PlainText,
                DeleteSource: false
            )
        );
        service.Stop();

        var item = Assert.Single(processed);
        Assert.True(item.Success, item.ErrorMessage);
        Assert.Equal(Path.Join(outputPath, "meeting (1).txt"), item.OutputPath);
        Assert.Equal("Transcribed meeting.wav", File.ReadAllText(item.OutputPath));
        Assert.True(Directory.Exists(Path.Join(outputPath, "meeting.txt")));
    }

    private static async Task<IReadOnlyList<WatchFolderHistoryItem>>
        StartAndWaitForProcessedItemsAsync(
            WatchFolderService service,
            int expectedCount,
            WatchFolderOptions options
        )
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var items = new ConcurrentQueue<WatchFolderHistoryItem>();

        void OnFileProcessed(object? sender, WatchFolderHistoryItem item)
        {
            items.Enqueue(item);
            if (items.Count >= expectedCount)
            {
                completion.TrySetResult(true);
            }
        }

        service.FileProcessed += OnFileProcessed;
        try
        {
            service.Start(options, TranscribeAsync);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return items.ToList();
        }
        finally
        {
            service.FileProcessed -= OnFileProcessed;
        }
    }

    private static Task<WatchFolderTranscriptionResult> TranscribeAsync(
        WatchFolderTranscriptionRequest request,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            new WatchFolderTranscriptionResult(
                $"Transcribed {Path.GetFileName(request.FilePath)}",
                "en",
                1,
                0.1,
                [],
                "fake",
                "test"
            )
        );
    }
}
