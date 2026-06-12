using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class FileTranscriptionSectionViewModelTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "TypeWhisper.FileQueue.Tests_" + Guid.NewGuid().ToString("N"));

    public FileTranscriptionSectionViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    [Fact]
    public void ClearQueue_RemovesTerminalItems_KeepsActiveAndQueued()
    {
        var vm = CreateViewModel();
        AddItem(vm, "done.wav", FileTranscriptionQueueItemStatus.Completed);
        AddItem(vm, "cancel.wav", FileTranscriptionQueueItemStatus.Cancelled);
        AddItem(vm, "boom.wav", FileTranscriptionQueueItemStatus.Error);
        AddItem(vm, "weird.xyz", FileTranscriptionQueueItemStatus.Unsupported);
        AddItem(vm, "waiting.wav", FileTranscriptionQueueItemStatus.Queued);
        AddItem(vm, "running.wav", FileTranscriptionQueueItemStatus.Transcribing);

        Assert.True(vm.HasClearableItems);

        vm.ClearQueueCommand.Execute(null);

        var remaining = vm.Items.Select(i => i.Status).ToList();
        Assert.Equal(
            new[]
            {
                FileTranscriptionQueueItemStatus.Queued,
                FileTranscriptionQueueItemStatus.Transcribing,
            },
            remaining);
        Assert.False(vm.HasClearableItems);
    }

    [Fact]
    public void HasClearableItems_FalseWhenNoTerminalItems()
    {
        var vm = CreateViewModel();
        AddItem(vm, "waiting.wav", FileTranscriptionQueueItemStatus.Queued);

        Assert.False(vm.HasClearableItems);
    }

    private static void AddItem(
        FileTranscriptionSectionViewModel vm,
        string name,
        FileTranscriptionQueueItemStatus status
    ) => vm.Items.Add(new FileTranscriptionQueueItemViewModel(name, status));

    private FileTranscriptionSectionViewModel CreateViewModel()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var commands = new SystemCommandAvailabilityService();
        var audioFiles = new AudioFileService(commands);
        var watchFolder = new WatchFolderService();
        return new FileTranscriptionSectionViewModel(new StubProcessor(), settings, audioFiles, watchFolder);
    }

    private sealed class StubProcessor : IFileTranscriptionProcessor
    {
        public Task<FileTranscriptionProcessResult> ProcessAsync(
            string filePath,
            Action<FileTranscriptionProcessProgress> onProgress,
            FileTranscriptionProcessOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
