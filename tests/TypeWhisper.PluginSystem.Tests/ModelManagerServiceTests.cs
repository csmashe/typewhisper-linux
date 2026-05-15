using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerServiceTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public ModelManagerServiceTests()
    {
        _profiles.Setup(p => p.Profiles).Returns([]);
    }

    [Fact]
    public void Engine_WithoutActiveModel_DoesNotFallbackToArbitraryConfiguredPlugin()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = ModelManagerService.GetPluginModelId("com.typewhisper.sherpa-onnx", "parakeet")
        });

        var pluginManager = CreatePluginManager(
            new FakeTranscriptionPlugin("com.typewhisper.openai-compatible", configured: true, selectedModelId: "whisper"),
            new FakeTranscriptionPlugin("com.typewhisper.sherpa-onnx", configured: true, selectedModelId: null));

        var sut = new ModelManagerService(pluginManager, _settings.Object);

        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);
        Assert.False(sut.Engine.IsModelLoaded);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_LoadsSelectedModel_WhenNoActiveModelExists()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(modelId, plugin.SelectedModelId);
        Assert.Equal(modelId, plugin.LastLoadedModelId);
        Assert.True(sut.Engine.IsModelLoaded);
    }

    [Fact]
    public async Task AcquireTranscriptionAsync_ReturnsLease_PinningLoadedPlugin()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);

        await using var lease = await sut.AcquireTranscriptionAsync(fullModelId);

        Assert.Same(plugin, lease.Plugin);
        Assert.Equal(fullModelId, sut.ActiveModelId);
    }

    [Fact]
    public async Task AcquireTranscriptionAsync_BlocksSecondAcquire_UntilLeaseDisposed()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        var lease1 = await sut.AcquireTranscriptionAsync(fullModelId);

        var secondAcquire = sut.AcquireTranscriptionAsync(fullModelId);
        Assert.False(secondAcquire.IsCompleted);

        await lease1.DisposeAsync();

        var lease2 = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease2);
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireTranscriptionAsync_ReturnsNull_WhileLeaseHeld()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        await using var lease = await sut.AcquireTranscriptionAsync(fullModelId);

        var attempt = await sut.TryAcquireTranscriptionAsync(fullModelId);

        Assert.Null(attempt);
    }

    [Fact]
    public async Task TranscriptionLease_DoubleDispose_ReleasesLockOnlyOnce()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        var lease = await sut.AcquireTranscriptionAsync(fullModelId);
        await lease.DisposeAsync();
        await lease.DisposeAsync(); // must not over-release (SemaphoreFullException) the (1,1) lock

        // The lock was released exactly once: one acquire succeeds, a second is blocked.
        var leaseA = await sut.AcquireTranscriptionAsync(fullModelId);
        var blocked = await sut.TryAcquireTranscriptionAsync(fullModelId);
        Assert.Null(blocked);
        await leaseA.DisposeAsync();
    }

    [Fact]
    public async Task AcquireTranscriptionAsync_FailedLoad_DoesNotLeakLock()
    {
        var sut = CreateServiceWithLoadableModel(out var goodModelId, out var plugin);

        var unknownModelId = ModelManagerService.GetPluginModelId("com.typewhisper.nonexistent", "ghost");
        await Assert.ThrowsAsync<ArgumentException>(() => sut.AcquireTranscriptionAsync(unknownModelId));

        // The failed acquire released the lock — a subsequent valid acquire still succeeds.
        await using var lease = await sut.AcquireTranscriptionAsync(goodModelId);
        Assert.Same(plugin, lease.Plugin);
    }

    [Fact]
    public async Task LoadModelAsync_BlockedWhileLeaseHeld_UntilLeaseDisposed()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        var lease = await sut.AcquireTranscriptionAsync(fullModelId);

        var load = sut.LoadModelAsync(fullModelId);
        Assert.False(load.IsCompleted);

        await lease.DisposeAsync();

        await load.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteModelAsync_BlockedWhileLeaseHeld_UntilLeaseDisposed()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        var lease = await sut.AcquireTranscriptionAsync(fullModelId);

        var delete = sut.DeleteModelAsync(fullModelId);
        Assert.False(delete.IsCompleted);

        await lease.DisposeAsync();

        await delete.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_BlockedWhileLeaseHeld_UntilLeaseDisposed()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out _);

        var lease = await sut.AcquireTranscriptionAsync(fullModelId);

        var ensure = sut.EnsureModelLoadedAsync(fullModelId);
        Assert.False(ensure.IsCompleted);

        await lease.DisposeAsync();

        Assert.True(await ensure.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AcquireTranscriptionAsync_BlocksWhileLoadModelInFlight()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);
        var fake = (FakeTranscriptionPlugin)plugin;
        fake.LoadGate = new TaskCompletionSource();

        // The load grabs _modelLock and parks inside plugin.LoadModelAsync.
        var load = sut.LoadModelAsync(fullModelId);
        await fake.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var acquire = sut.AcquireTranscriptionAsync(fullModelId);
        Assert.False(acquire.IsCompleted);

        fake.LoadGate.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireTranscriptionAsync_ReturnsNull_AndDoesNotLoad_WhenModelNotActive()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);
        var fake = (FakeTranscriptionPlugin)plugin;

        // No model loaded yet — a best-effort try-acquire must never initiate a load.
        var attempt = await sut.TryAcquireTranscriptionAsync(fullModelId);

        Assert.Null(attempt);
        Assert.Null(sut.ActiveModelId);
        Assert.Null(fake.LastLoadedModelId);
    }

    [Fact]
    public async Task TryAcquireTranscriptionAsync_Succeeds_WhenRequestedModelAlreadyActive()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);
        await using (await sut.AcquireTranscriptionAsync(fullModelId)) { }

        await using var attempt = await sut.TryAcquireTranscriptionAsync(fullModelId);

        Assert.NotNull(attempt);
        Assert.Same(plugin, attempt!.Plugin);
    }

    [Fact]
    public async Task TryAcquireTranscriptionAsync_ReturnsNull_WhenDifferentModelActive()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);
        await using (await sut.AcquireTranscriptionAsync(fullModelId)) { }
        var fake = (FakeTranscriptionPlugin)plugin;

        var otherModelId = ModelManagerService.GetPluginModelId("com.typewhisper.sherpa-onnx", "whisper");
        var attempt = await sut.TryAcquireTranscriptionAsync(otherModelId);

        // A different requested model must skip silently, never swap the active model.
        Assert.Null(attempt);
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal("parakeet", fake.LastLoadedModelId);
    }

    [Fact]
    public async Task UnloadModelAsync_HoldsModelLock_UntilPluginUnloadCompletes()
    {
        var sut = CreateServiceWithLoadableModel(out var fullModelId, out var plugin);
        var fake = (FakeTranscriptionPlugin)plugin;
        await using (await sut.AcquireTranscriptionAsync(fullModelId)) { }

        fake.UnloadGate = new TaskCompletionSource();
        var unload = sut.UnloadModelAsync();
        await fake.UnloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Native teardown is still in flight — the model lock must stay held.
        var acquire = sut.AcquireTranscriptionAsync(fullModelId);
        Assert.False(acquire.IsCompleted);

        fake.UnloadGate.SetResult();
        await unload.WaitAsync(TimeSpan.FromSeconds(5));

        await using var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task UnloadModelAsync_AfterDispose_DoesNotThrowObjectDisposedException()
    {
        var sut = CreateServiceWithLoadableModel(out _, out _);

        sut.Dispose();

        // A fire-and-forget unload may still race teardown — it must not throw
        // ObjectDisposedException because the model lock was disposed.
        var exception = await Record.ExceptionAsync(() => sut.UnloadModelAsync());

        Assert.IsNotType<ObjectDisposedException>(exception);
    }

    private ModelManagerService CreateServiceWithLoadableModel(
        out string fullModelId, out ITranscriptionEnginePlugin plugin)
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings.Setup(s => s.Current).Returns(new AppSettings { SelectedModelId = fullModelId });

        var fake = new FakeTranscriptionPlugin(
            pluginId, configured: true, selectedModelId: null, supportsModelDownload: true);
        plugin = fake;
        return new ModelManagerService(CreatePluginManager(fake), _settings.Object);
    }

    private PluginManager CreatePluginManager(params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []);

        SetPrivateField(pluginManager, "_transcriptionEngines", transcriptionEngines.ToList());
        return pluginManager;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            bool configured,
            string? selectedModelId,
            bool supportsModelDownload = false)
        {
            PluginId = pluginId;
            IsConfigured = configured;
            SelectedModelId = selectedModelId;
            SupportsModelDownload = supportsModelDownload;
            TranscriptionModels = [new PluginModelInfo("parakeet", "Parakeet"), new PluginModelInfo("whisper", "Whisper")];
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName => PluginId;
        public bool IsConfigured { get; }
        public bool SupportsModelDownload { get; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public string? LastLoadedModelId { get; private set; }

        /// <summary>Completes once <see cref="LoadModelAsync"/> has begun.</summary>
        public TaskCompletionSource LoadStarted { get; } = new();

        /// <summary>When set, <see cref="LoadModelAsync"/> parks until it completes.</summary>
        public TaskCompletionSource? LoadGate { get; set; }

        /// <summary>Completes once <see cref="UnloadModelAsync"/> has begun.</summary>
        public TaskCompletionSource UnloadStarted { get; } = new();

        /// <summary>When set, <see cref="UnloadModelAsync"/> parks until it completes.</summary>
        public TaskCompletionSource? UnloadGate { get; set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadStarted.TrySetResult();
            if (LoadGate is not null)
                await LoadGate.Task.WaitAsync(ct);
            LastLoadedModelId = modelId;
            SelectedModelId = modelId;
        }

        public async Task UnloadModelAsync()
        {
            UnloadStarted.TrySetResult();
            if (UnloadGate is not null)
                await UnloadGate.Task;
            SelectedModelId = null;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }
}
