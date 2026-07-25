using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class FileTranscriptionSectionViewModelTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.FileTranscriptionSectionViewModelTests"
    );
    private readonly List<PluginManager> _pluginManagers = [];

    public void Dispose()
    {
        foreach (var pluginManager in _pluginManagers)
        {
            pluginManager.Dispose();
        }

        try
        {
            TestPaths.DeleteDirectory(_tempDir);
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

    [Fact]
    public void Constructor_WithConfiguredAutoStart_WaitsForExplicitEntryPoint()
    {
        var watchPath = Path.Join(_tempDir, "configured-auto-start");
        Directory.CreateDirectory(watchPath);
        var settings = CreateSettingsWithWatchFolder(watchPath, autoStart: true);
        var vm = CreateViewModel(settings);

        try
        {
            Assert.False(vm.IsWatchFolderRunning);

            vm.TryAutoStartWatchFolder();

            Assert.True(vm.IsWatchFolderRunning);
            Assert.Equal(watchPath, vm.WatchFolderPath);
        }
        finally
        {
            vm.StopWatchFolderCommand.Execute(null);
        }
    }

    [Fact]
    public void Constructor_WithPoisonedAutoStartPath_DoesNotThrow()
    {
        var settings = CreateSettingsWithPoisonedWatchFolder(autoStart: true);
        FileTranscriptionSectionViewModel? vm = null;

        var exception = Record.Exception(() => vm = CreateViewModel(settings));

        Assert.Null(exception);
        Assert.NotNull(vm);
        Assert.False(vm.IsWatchFolderRunning);
    }

    [Fact]
    public async Task AutoStart_WhenPluginCapabilitiesAreEmpty_ClassifiesReadinessBeforeProcessor()
    {
        var watchPath = Path.Join(_tempDir, "not-ready-watch");
        var outputPath = Path.Join(_tempDir, "not-ready-output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        File.WriteAllBytes(Path.Join(watchPath, "waiting.wav"), [1, 2, 3]);
        var settings = CreateSettingsWithWatchFolder(watchPath, autoStart: true);
        settings.Save(
            settings.Current with
            {
                WatchFolderOutputPath = outputPath,
            }
        );
        var processor = new CountingProcessor();
        var watchFolder = new WatchFolderService(
            Path.Join(_tempDir, "not-ready-data"),
            readinessRetryDelay: static (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        );
        var completion = new TaskCompletionSource<WatchFolderHistoryItem>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        watchFolder.FileProcessed += (_, item) => completion.TrySetResult(item);
        var vm = CreateViewModel(settings, processor, watchFolder);

        try
        {
            vm.TryAutoStartWatchFolder();

            var item = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(item.Success);
            Assert.Equal("Transcription engines are not ready.", item.ErrorMessage);
            Assert.Equal(0, processor.CallCount);
            Assert.Single(watchFolder.CurrentRun!.FailedFingerprints);
        }
        finally
        {
            await watchFolder.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await watchFolder.DisposeAsync();
        }
    }

    [Fact]
    public void StartWatchFolder_WithPoisonedPath_ShowsErrorAndCanRecover()
    {
        var settings = CreateSettingsWithPoisonedWatchFolder(autoStart: false);
        var vm = CreateViewModel(settings);

        var exception = Record.Exception(() => vm.StartWatchFolderCommand.Execute(null));

        Assert.Null(exception);
        Assert.False(vm.IsWatchFolderRunning);
        Assert.NotNull(vm.WatchFolderStartError);
        Assert.Equal(
            Loc.Instance.GetString(
                "FileTranscription.WatchFolderStartFailed",
                vm.WatchFolderStartError
            ),
            vm.WatchFolderStatusText
        );

        var repairedPath = Path.Join(_tempDir, "repaired-watch-folder");
        Directory.CreateDirectory(repairedPath);
        vm.SetWatchFolderPath(repairedPath);
        vm.StartWatchFolderCommand.Execute(null);

        Assert.True(vm.IsWatchFolderRunning);
        Assert.Null(vm.WatchFolderStartError);
        Assert.Equal(
            Loc.Instance["FileTranscription.WatchingForNewFiles"],
            vm.WatchFolderStatusText
        );

        vm.StopWatchFolderCommand.Execute(null);
    }

    [Fact]
    public void StopWatchFolder_AfterFailedStart_ClearsError()
    {
        var settings = CreateSettingsWithPoisonedWatchFolder(autoStart: false);
        var vm = CreateViewModel(settings);
        vm.StartWatchFolderCommand.Execute(null);

        Assert.NotNull(vm.WatchFolderStartError);

        vm.StopWatchFolderCommand.Execute(null);

        Assert.False(vm.IsWatchFolderRunning);
        Assert.Null(vm.WatchFolderStartError);
        Assert.Equal(Loc.Instance["FileTranscription.Stopped"], vm.WatchFolderStatusText);
    }

    private static void AddItem(
        FileTranscriptionSectionViewModel vm,
        string name,
        FileTranscriptionQueueItemStatus status
    ) => vm.Items.Add(new FileTranscriptionQueueItemViewModel(name, status));

    private SettingsService CreateSettingsWithPoisonedWatchFolder(bool autoStart)
    {
        var poisonedParent = Path.Join(_tempDir, $"poisoned-parent-{Guid.NewGuid():N}");
        File.WriteAllText(poisonedParent, "not a directory");

        var settings = new SettingsService(Path.Join(_tempDir, $"settings-{Guid.NewGuid():N}.json"));
        settings.Save(
            settings.Current with
            {
                WatchFolderPath = Path.Join(poisonedParent, "watch-folder"),
                WatchFolderAutoStart = autoStart,
            }
        );
        return settings;
    }

    private SettingsService CreateSettingsWithWatchFolder(string watchPath, bool autoStart)
    {
        var settings = new SettingsService(
            Path.Join(_tempDir, $"settings-{Guid.NewGuid():N}.json")
        );
        settings.Save(
            settings.Current with
            {
                WatchFolderPath = watchPath,
                WatchFolderAutoStart = autoStart,
            }
        );
        return settings;
    }

    private FileTranscriptionSectionViewModel CreateViewModel(
        SettingsService? settings = null,
        IFileTranscriptionProcessor? processor = null,
        WatchFolderService? watchFolder = null
    )
    {
        settings ??= new SettingsService(Path.Join(_tempDir, "settings.json"));
        var commands = new SystemCommandAvailabilityService();
        var audioFiles = new AudioFileService(commands);
        watchFolder ??= new WatchFolderService(
            Path.Join(_tempDir, $"watch-folder-data-{Guid.NewGuid():N}")
        );
        var pluginManager = TestPluginManagerFactory.Create();
        _pluginManagers.Add(pluginManager);
        return new FileTranscriptionSectionViewModel(
            processor ?? new StubProcessor(),
            settings,
            audioFiles,
            watchFolder,
            pluginManager
        );
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

    private sealed class CountingProcessor : IFileTranscriptionProcessor
    {
        public int CallCount { get; private set; }

        public Task<FileTranscriptionProcessResult> ProcessAsync(
            string filePath,
            Action<FileTranscriptionProcessProgress> onProgress,
            FileTranscriptionProcessOptions? options,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            throw new InvalidOperationException("Processor should not be invoked before readiness.");
        }
    }
}
