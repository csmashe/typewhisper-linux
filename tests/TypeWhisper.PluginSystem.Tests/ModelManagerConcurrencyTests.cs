using Moq;
using System.Collections.Concurrent;
using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs downloads, cancels, loads, selections, deletes and dictation leases against each other
///     at random, with failing downloads and storage, and checks what the service promises: one file
///     operation per model at a time, no load of files still being written, no deadlock, and no
///     download or status left in progress.
/// </summary>
public sealed class ModelManagerConcurrencyTests
{
    private const string PluginId = "com.test.stress";
    private static readonly string[] s_models = ["a", "b"];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RandomOperations_KeepModelFilesConsistent(int seed)
    {
        var random = new Random(seed);
        var engine = new StressEngine(seed);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = FullId("a"),
            ModelAutoUnloadSeconds = 0,
        });
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.Profiles).Returns([]);
        using var pluginManager = new PluginManager(
            new PluginLoader(TestPaths.NewTempPath("TypeWhisper.ModelManagerConcurrencyTests")),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object,
            []);
        typeof(PluginManager).GetField("_transcriptionEngines", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pluginManager, new List<ITranscriptionEngineRole> { engine });
        using var sut = new ModelManagerService(pluginManager, settings.Object)
        {
            CudaRuntimePreflight = () => (false, "CUDA not available in test"),
        };
        var unexpected = new ConcurrentQueue<Exception>();

        for (var round = 0; round < 60; round++)
        {
            var operations = Enumerable.Range(0, 8)
                .Select(_ => RunRandomOperation(sut, random.Next(9), FullId(s_models[random.Next(2)]), random.Next(4)))
                .Select(operation => Observe(operation, unexpected))
                .ToArray();

            var all = Task.WhenAll(operations);
            Assert.True(
                await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None)) == all,
                $"Round {round} did not finish; an operation is stuck.");
        }

        Assert.Empty(unexpected);
        Assert.Empty(engine.Violations);
        Assert.False(sut.IsAnyModelDownloading);
        foreach (var model in s_models)
        {
            Assert.False(
                sut.GetTrackedStatus(FullId(model))?.Type is ModelStatusType.Downloading or ModelStatusType.Loading,
                $"{model} was left {sut.GetTrackedStatus(FullId(model))?.Type}.");
        }
    }

    private static string FullId(string model) => ModelManagerService.GetPluginModelId(PluginId, model);

    private static async Task RunRandomOperation(ModelManagerService sut, int kind, string modelId, int delayMs)
    {
        using var cancellation = new CancellationTokenSource();
        if (delayMs == 3)
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(2));
        }

        await Task.Delay(delayMs, CancellationToken.None);
        switch (kind)
        {
            case 0:
                await sut.DownloadModelAsync(modelId, cancellation.Token);
                break;
            case 1:
                sut.CancelDownload(modelId);
                break;
            case 2:
                await sut.LoadModelAsync(modelId, cancellation.Token);
                break;
            case 3:
                await sut.DownloadAndLoadModelAsync(modelId, cancellation.Token);
                break;
            case 4:
                await sut.TryDeleteModelAsync(modelId, cancellation.Token);
                break;
            case 5:
                await sut.DeleteModelAsync(modelId, cancellation.Token);
                break;
            case 6:
                await using (await sut.AcquireTranscriptionAsync(modelId, cancellationToken: cancellation.Token))
                {
                    await Task.Delay(1, CancellationToken.None);
                }

                break;
            case 7:
                await sut.TryLoadModelAsync(modelId, cancellation.Token);
                break;
            default:
                await sut.TryDownloadAndLoadModelAsync(modelId, cancellation.Token);
                break;
        }
    }

    // Cancellations, injected download faults, a delete refused while downloading, and a load of a
    // model that is not on disk are expected outcomes; anything else is a bug.
    private static async Task Observe(Task operation, ConcurrentQueue<Exception> unexpected)
    {
        try
        {
            await operation;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            unexpected.Enqueue(ex);
        }
    }

    private sealed class StressEngine(int seed) : ITranscriptionEngineRole
    {
        private readonly Lock _gate = new();
        private readonly Random _random = new(seed);
        private readonly HashSet<string> _downloaded = [];
        private readonly Dictionary<string, int> _writers = [];

        public ConcurrentQueue<string> Violations { get; } = new();

        public string PluginId => ModelManagerConcurrencyTests.PluginId;
        public string ProviderId => "stress";
        public string ProviderDisplayName => "Stress";
        public bool IsConfigured => true;
        public bool SupportsModelDownload => true;
        public bool SupportsTranslation => false;
        public string? SelectedModelId { get; private set; }

        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new("a", "A"), new("b", "B")];

        public bool IsModelDownloaded(string modelId)
        {
            lock (_gate)
            {
                // Storage can become unavailable at any moment.
                if (_random.Next(25) == 0)
                {
                    throw new IOException("injected storage failure");
                }

                return _downloaded.Contains(modelId);
            }
        }

        public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        {
            Enter(modelId, "download");
            try
            {
                progress?.Report(0.5);
                await Task.Delay(NextDelay(), ct);
                if (NextFault())
                {
                    throw new IOException("injected download failure");
                }

                lock (_gate)
                {
                    _downloaded.Add(modelId);
                }
            }
            finally
            {
                Leave(modelId);
            }
        }

        public async Task DeleteModelAsync(string modelId, CancellationToken ct)
        {
            Enter(modelId, "delete");
            try
            {
                await Task.Yield();
                lock (_gate)
                {
                    _downloaded.Remove(modelId);
                }
            }
            finally
            {
                Leave(modelId);
            }
        }

        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writers.GetValueOrDefault(modelId) > 0)
                {
                    Violations.Enqueue($"load of {modelId} while its files were being written");
                }

                if (!_downloaded.Contains(modelId))
                {
                    throw new FileNotFoundException($"{modelId} is not downloaded.");
                }
            }

            await Task.Delay(NextDelay(), ct);
            SelectedModelId = modelId;
        }

        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task UnloadModelAsync() => Task.CompletedTask;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", "en", 1));

        private void Enter(string modelId, string operation)
        {
            lock (_gate)
            {
                var writers = _writers.GetValueOrDefault(modelId) + 1;
                _writers[modelId] = writers;
                if (writers > 1)
                {
                    Violations.Enqueue($"{operation} of {modelId} overlapped another file operation");
                }
            }
        }

        private void Leave(string modelId)
        {
            lock (_gate)
            {
                _writers[modelId]--;
            }
        }

        private int NextDelay()
        {
            lock (_gate)
            {
                return _random.Next(0, 4);
            }
        }

        private bool NextFault()
        {
            lock (_gate)
            {
                return _random.Next(6) == 0;
            }
        }
    }
}
