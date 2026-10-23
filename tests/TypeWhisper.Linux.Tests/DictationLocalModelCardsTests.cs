using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationLocalModelCardsTests
{
    [Fact]
    public void Cards_ListOnlyDownloadableModelsWithSizeAndState()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local, new CloudEngine());

        Assert.Equal(["Small", "Large", "Medium"], context.Sut.LocalModelCards.Select(card => card.DisplayName));
        var small = context.Card("small");
        var large = context.Card("large");
        Assert.Equal("~200 MB · Local engine", small.DetailText);
        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloaded"], small.BadgeText);
        Assert.True(small is { ShowUse: true, CanUse: true, ShowRemove: true, ShowDownload: false });
        Assert.Equal(Loc.Instance["Dictation.ModelCardRecommended"], large.BadgeText);
        Assert.True(large is { ShowDownload: true, CanDownload: true, ShowUse: false, ShowRemove: false });
        Assert.True(context.Sut.HasLocalModelCards);
    }

    [Fact]
    public void Toggle_ListsModelsThatAreNeitherDownloadedNorRecommended()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);

        Assert.True(context.Card("small").IsListed);
        Assert.True(context.Card("large").IsListed);
        Assert.False(context.Card("medium").IsListed);
        Assert.True(context.Sut.ShowLocalModelsToggle);
        Assert.Equal(Loc.Instance.GetString("Dictation.ShowAllLocalModels", 3), context.Sut.LocalModelsToggleText);

        context.Sut.ToggleLocalModelsCommand.Execute(null);

        Assert.True(context.Card("medium").IsListed);
        Assert.Equal(Loc.Instance["Dictation.ShowFewerLocalModels"], context.Sut.LocalModelsToggleText);
    }

    [Fact]
    public async Task Download_ShowsProgressAndCancelThenLeavesSelectionAlone()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var large = context.Card("large");

        var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        local.LastProgress!.Report(0.25);
        await context.WaitUntilAsync(() => large.PercentText == "25%");

        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloading"], large.BadgeText);
        Assert.True(large is { ShowProgress: true, IsProgressIndeterminate: false, ShowCancel: true, ShowDownload: false });
        Assert.False(context.Card("small").CanDownload);
        Assert.False(context.Card("small").CanRemove);
        Assert.False(context.Sut.CanChangeModelStorage);

        local.DownloadGate.SetResult();
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Loc.Instance["Dictation.ModelCardDownloaded"], large.BadgeText);
        Assert.True(large is { ShowProgress: false, ShowCancel: false, ShowUse: true, ShowRemove: true });
        Assert.True(context.Sut.CanChangeModelStorage);
        Assert.Null(context.Sut.SelectedModel);
        Assert.Equal(0, local.LoadCount);
        Assert.Equal(Loc.Instance.GetString("Dictation.ModelDownloaded", "Large"), context.Sut.StatusText);
    }

    [Fact]
    public async Task Cancel_StopsDownloadAndRestoresCard()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        using var context = new CardsTestContext(local);
        var large = context.Card("large");

        var download = context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.WaitUntilAsync(() => large.ShowCancel);
        context.Sut.CancelLocalModelDownloadCommand.Execute(large);
        await download.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Loc.Instance["Dictation.ModelCardRecommended"], large.BadgeText);
        Assert.True(large is { ShowDownload: true, CanDownload: true, ShowProgress: false, ShowCancel: false });
        Assert.DoesNotContain("large", local.Downloaded);
        Assert.Equal(Loc.Instance.GetString("Dictation.ModelDownloadCanceled", "Large"), context.Sut.StatusText);
    }

    [Fact]
    public async Task Use_SelectsAndLoadsTheModel()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var small = context.Card("small");

        context.Sut.UseLocalModelCommand.Execute(small);
        await context.WaitUntilAsync(() => local.LoadCount == 1 && small.IsActive);

        Assert.Equal(small.ModelId, context.Sut.SelectedModel?.ModelId);
        Assert.Equal(small.ModelId, context.Settings.Object.Current.SelectedModelId);
        Assert.Equal(Loc.Instance["Dictation.ModelCardActive"], small.BadgeText);
        Assert.Equal(Loc.Instance["Dictation.ModelCardInUse"], small.UseText);
        Assert.False(small.CanUse);
    }

    [Fact]
    public async Task Remove_DeletesAnotherModelAndKeepsTheSelection()
    {
        var local = new DownloadableEngine();
        local.Downloaded.UnionWith(["small", "large"]);
        using var context = new CardsTestContext(local);
        context.Sut.UseLocalModelCommand.Execute(context.Card("small"));
        await context.WaitUntilAsync(() => context.Card("small").IsActive);
        var large = context.Card("large");

        await context.Sut.DeleteLocalModelAsync(large);

        Assert.DoesNotContain("large", local.Downloaded);
        Assert.True(large.ShowDownload);
        Assert.Equal(context.Card("small").ModelId, context.Sut.SelectedModel?.ModelId);
    }

    [Fact]
    public async Task Download_OfTheSelectedModel_AlsoLoadsIt()
    {
        var local = new DownloadableEngine { DownloadGate = new TaskCompletionSource() };
        using var context = new CardsTestContext(local);
        var large = context.Card("large");
        context.Sut.SelectedModel = context.Sut.ModelOptions.Single(option => option.ModelId == large.ModelId);
        await local.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.WaitUntilAsync(() => large.ShowCancel);
        context.Sut.CancelLocalModelDownloadCommand.Execute(large);
        await context.WaitUntilAsync(() => large.ShowDownload);
        local.DownloadGate.SetResult();

        await context.Sut.DownloadLocalModelCommand.ExecuteAsync(large);
        await context.WaitUntilAsync(() => large.IsActive);

        Assert.Equal(1, local.LoadCount);
        Assert.Equal(large.ModelId, context.Models.ActiveModelId);
    }

    [Fact]
    public void UnavailableStorage_ShowsCardsAsNotDownloaded()
    {
        var local = new DownloadableEngine { StorageUnavailable = true };
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);

        Assert.True(context.Card("small").ShowDownload);
        Assert.False(context.Card("small").ShowRemove);
    }

    [Fact]
    public async Task DeleteOutsideTheViewModel_UpdatesTheCard()
    {
        var local = new DownloadableEngine();
        local.Downloaded.Add("small");
        using var context = new CardsTestContext(local);
        var small = context.Card("small");
        Assert.True(small.ShowRemove);

        Assert.Equal(ModelDeleteOutcome.Deleted, await context.Models.TryDeleteModelAsync(small.ModelId, CancellationToken.None));
        await context.WaitUntilAsync(() => small.ShowDownload);

        Assert.False(small.ShowRemove);
        Assert.Equal(Loc.Instance["Dictation.ModelCardNotDownloaded"], small.BadgeText);
    }

    private sealed class CardsTestContext : IDisposable
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        private readonly AudioRecordingService _audio;
        private readonly PluginManager _pluginManager;

        public CardsTestContext(params ITranscriptionEngineRole[] engines)
        {
            Settings = TestPluginManagerFactory.CreateSettings(AppSettings.Default with { SelectedModelId = null });
            _pluginManager = TestPluginManagerFactory.Create();
            PluginManagerTestAccess.SetTranscriptionEngines(_pluginManager, engines);
            var commands = new SystemCommandAvailabilityService(new FakeProcessRunner());
            Models = new ModelManagerService(_pluginManager, Settings.Object, commands)
            {
                CudaRuntimePreflight = () => (false, "CUDA not available in test"),
            };
            var devices = new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true));
            _audio = new AudioRecordingService(
                devices.GetDevices, static _ => { }, static () => 0, static () => { },
                postToUiThread: static _ => { });
            // ReSharper disable once InconsistentNaming -- "a11y" is the standard numeronym for accessibility.
            var a11yBus = new Mock<IAccessibilityBusActivation>();
            a11yBus.Setup(bus => bus.IsActivatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            Sut = new DictationSectionViewModel(
                (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(DictationOrchestrator)),
                Models,
                _audio,
                Settings.Object,
                _pluginManager,
                commands,
                new CudaLibraryPathSetupService(),
                a11yBus.Object,
                devices.GetDevices,
                dispatchNotification: _pending.Enqueue);
        }

        public DictationSectionViewModel Sut { get; }
        public ModelManagerService Models { get; }
        public Mock<ISettingsService> Settings { get; }

        public LocalModelCard Card(string modelId) =>
            Sut.LocalModelCards.Single(card => card.ModelId.EndsWith(":" + modelId, StringComparison.Ordinal));

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                while (_pending.TryDequeue(out var job))
                {
                    job();
                }

                if (predicate())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.True(predicate(), "Condition was not reached before the test deadline.");
        }

        public void Dispose()
        {
            _audio.Dispose();
            Models.Dispose();
            _pluginManager.Dispose();
        }
    }

    private sealed class DownloadableEngine : ITranscriptionEngineRole
    {
        public HashSet<string> Downloaded { get; } = [];
        public TaskCompletionSource? DownloadGate { get; init; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<double>? LastProgress { get; private set; }
        public int LoadCount { get; private set; }

        public string PluginId => "com.test.local";
        public string ProviderId => "local";
        public string ProviderDisplayName => "Local engine";
        public bool IsConfigured => true;
        public bool SupportsModelDownload => true;
        public bool SupportsTranslation => false;
        public string? SelectedModelId { get; private set; }

        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [
            new("small", "Small") { SizeDescription = "~200 MB" },
            new("large", "Large") { IsRecommended = true },
            new("medium", "Medium"),
        ];

        public bool StorageUnavailable { get; init; }

        public bool IsModelDownloaded(string modelId)
        {
            if (StorageUnavailable)
            {
                throw new IOException("The model folder is not available.");
            }

            lock (Downloaded)
            {
                return Downloaded.Contains(modelId);
            }
        }

        public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        {
            LastProgress = progress;
            DownloadStarted.TrySetResult();
            if (DownloadGate is not null)
            {
                await DownloadGate.Task.WaitAsync(ct);
            }

            lock (Downloaded)
            {
                Downloaded.Add(modelId);
            }
        }

        public Task DeleteModelAsync(string modelId, CancellationToken ct)
        {
            lock (Downloaded)
            {
                Downloaded.Remove(modelId);
            }

            return Task.CompletedTask;
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCount++;
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task UnloadModelAsync() => Task.CompletedTask;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CloudEngine : ITranscriptionEngineRole
    {
        public string PluginId => "com.test.cloud";
        public string ProviderId => "cloud";
        public string ProviderDisplayName => "Cloud engine";
        public bool IsConfigured => true;
        public bool SupportsTranslation => false;
        public string? SelectedModelId => null;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("cloud-1", "Cloud one")];

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
