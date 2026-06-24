using Moq;
using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerServiceTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();

    public ModelManagerServiceTests()
    {
        _profiles.Setup(p => p.Profiles).Returns([]);
    }

    [Fact]
    public void Engine_WithoutActiveModel_DoesNotFallbackToArbitraryConfiguredPlugin()
    {
        _settings
            .Setup(s => s.Current)
            .Returns(
                new AppSettings
                {
                    SelectedModelId = ModelManagerService.GetPluginModelId(
                        "com.typewhisper.sherpa-onnx",
                        "parakeet"
                    )
                }
            );

        var pluginManager = CreatePluginManager(
            new FakeTranscriptionPlugin(
                "com.typewhisper.openai-compatible",
                true,
                "whisper"
            ),
            new FakeTranscriptionPlugin(
                "com.typewhisper.sherpa-onnx",
                true,
                null
            )
        );

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

        _settings.Setup(s => s.Current).Returns(new AppSettings { SelectedModelId = fullModelId });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            true,
            null,
            true
        );
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

        var unknownModelId = ModelManagerService.GetPluginModelId(
            "com.typewhisper.nonexistent",
            "ghost"
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.AcquireTranscriptionAsync(unknownModelId)
        );

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

        var otherModelId = ModelManagerService.GetPluginModelId(
            "com.typewhisper.sherpa-onnx",
            "whisper"
        );
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

    [Theory]
    [InlineData(
        AppSettings.LocalModelAccelerationCpu,
        TranscriptionAccelerationPreference.Cpu
    )]
    [InlineData(
        AppSettings.LocalModelAccelerationAuto,
        TranscriptionAccelerationPreference.Cpu // Auto resolves to Cpu when CUDA preflight fails.
    )]
    [InlineData(
        AppSettings.LocalModelAccelerationNvidiaCuda,
        TranscriptionAccelerationPreference.NvidiaCuda
    )]
    public async Task LoadModelAsync_AppliesSavedAccelerationPreferenceBeforeLoading(
        string savedPreference,
        TranscriptionAccelerationPreference expectedPlugin)
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = savedPreference
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            // NvidiaCuda preference needs the preflight to succeed; Cpu/Auto don't care
            // about the success path. Set it to succeed so the explicit-NvidiaCuda case
            // doesn't throw, then verify the plugin saw the right resolved value.
            CudaRuntimePreflight = () => savedPreference == AppSettings.LocalModelAccelerationNvidiaCuda
                ? (true, "preflight ok")
                : (false, "no cuda"),
        };

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(expectedPlugin, fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_AutoPreferenceWithoutGpu_ResolvesToCpu_EvenWhenCudaLibsLoad()
    {
        // Regression: pre-fix, `TryPreloadCuda12RuntimeLibraries` succeeding was the
        // sole signal for Auto → NvidiaCuda. On a machine with the CUDA 12 runtime
        // installed but no NVIDIA GPU, that produced a whisper.cpp UseGpu=true load
        // that failed at runtime. The default preflight now gates on GPU presence
        // first; the test seam asserts the resolver respects a "no GPU" preflight.
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            // Simulates "CUDA libs present, GPU absent" — preflight reports failure.
            CudaRuntimePreflight = () => (false, "No NVIDIA GPU/driver detected."),
        };

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_AutoPreferenceResolvesViaPreflight_PluginSeesResolvedBackend()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () => (true, "preflight ok"),
        };

        await sut.LoadModelAsync(fullModelId);

        // Auto must resolve to a concrete backend before the plugin sees it.
        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            fake.AccelerationPreferenceAtLoad);
        Assert.NotEqual(
            TranscriptionAccelerationPreference.Auto,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_NvidiaCudaPreferenceWithoutCuda_Throws()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () => (false, "CUDA 12 runtime libraries are not installed."),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LoadModelAsync(fullModelId));
        Assert.Contains("CUDA", ex.Message);
        // Plugin must not have been told to load with NvidiaCuda — the throw happens first.
        Assert.Null(fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_NvidiaCudaPreference_SelfProvisioningPlugin_NoSystemCuda_LoadsWithCuda()
    {
        // A plugin that downloads + preloads its own CUDA runtime on demand must NOT be
        // hard-failed just because the host has no system CUDA install — that's exactly
        // the case its on-demand provisioner exists to handle. The host skips the
        // preflight entirely and lets the plugin provision (and fall back to CPU itself
        // if needed) during the load.
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true)
        {
            ProvisionsCudaRuntimeOnDemand = true,
        };
        var preflightCalls = 0;
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () =>
            {
                preflightCalls++;
                return (false, "CUDA 12 runtime libraries are not installed.");
            },
        };

        await sut.LoadModelAsync(fullModelId);

        // No preflight gate for self-provisioning plugins; the plugin receives the
        // explicit NvidiaCuda preference and handles provisioning/fallback itself.
        Assert.Equal(0, preflightCalls);
        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_AutoPreference_SelfProvisioningPlugin_NoSystemCuda_ResolvesToCpu()
    {
        // Auto stays conservative even for self-provisioning plugins: when no CUDA
        // runtime is already present, Auto resolves to CPU rather than silently kicking
        // off a large on-demand download. The big download only happens on an explicit
        // NvidiaCuda choice.
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true)
        {
            ProvisionsCudaRuntimeOnDemand = true,
        };
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () => (false, "CUDA 12 runtime libraries are not installed."),
        };

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_AutoPreferenceOnCpuOnlyPlugin_ResolvesToCpu_NoPreflight()
    {
        // SDK contract: plugins must never see TranscriptionAccelerationPreference.Auto.
        // For CPU-only plugins, the host resolves Auto → Cpu directly (no preflight
        // since there's no CUDA path to consider for this plugin).
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true)
        {
            SupportedAccelerationBackends = [TranscriptionAccelerationBackend.Cpu],
        };
        var preflightCalls = 0;
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () =>
            {
                preflightCalls++;
                return (false, "should not be called");
            },
        };

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(0, preflightCalls);
        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task LoadModelAsync_NvidiaCudaPreferenceOnCpuOnlyPlugin_LoadsWithCpu_NoPreflightThrow()
    {
        // SherpaOnnx / Granite report SupportedAccelerationBackends = [Cpu] only.
        // When the saved preference is NvidiaCuda (e.g. migrated from the legacy
        // computeBackend setting or shared across machines), the host must not
        // run the CUDA preflight + hard-error path — the plugin's own
        // SetAccelerationPreference already warns and falls back to CPU.
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true)
        {
            SupportedAccelerationBackends = [TranscriptionAccelerationBackend.Cpu],
        };
        var preflightCalls = 0;
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () =>
            {
                preflightCalls++;
                return (false, "should not be called");
            },
        };

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(0, preflightCalls);
        // Plugin sees the user's NvidiaCuda preference unchanged; its own
        // SetAccelerationPreference implementation is what falls back to CPU.
        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_PreferenceChange_TriggersReload()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");

        var currentSettings = new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        };
        _settings.Setup(s => s.Current).Returns(() => currentSettings);

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            // Make the preflight always succeed so the explicit-NvidiaCuda case
            // can take the load path (rather than throwing).
            CudaRuntimePreflight = () => (true, "ok"),
        };

        await sut.EnsureModelLoadedAsync(fullModelId);
        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            fake.AccelerationPreferenceAtLoad);

        currentSettings = currentSettings with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        };

        await sut.EnsureModelLoadedAsync(fullModelId);

        // The reload re-applied the new preference.
        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            fake.AccelerationPreferenceAtLoad);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_PreferenceUnchanged_DoesNotReload()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
            });

        var fake = new FakeTranscriptionPlugin(pluginId, true, null, true);
        var sut = new ModelManagerService(CreatePluginManager(fake), _settings.Object)
        {
            CudaRuntimePreflight = () => (false, "no cuda"),
        };

        await sut.EnsureModelLoadedAsync(fullModelId);
        var firstLoadCallCount = fake.SetAccelerationPreferenceCount;

        // Second EnsureModelLoadedAsync with no preference change: fast path.
        await sut.EnsureModelLoadedAsync(fullModelId);

        Assert.Equal(firstLoadCallCount, fake.SetAccelerationPreferenceCount);
    }

    [Fact]
    public async Task ClearCudaRuntimeCacheAsync_ClearsEveryProvisioningEngine_AndSkipsOthers()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings());

        var sherpa = new FakeTranscriptionPlugin("com.typewhisper.sherpa-onnx", true, null)
        {
            ProvisionsCudaRuntimeOnDemand = true,
        };
        var whisper = new FakeTranscriptionPlugin("com.typewhisper.whisper-cpp", true, null)
        {
            ProvisionsCudaRuntimeOnDemand = true,
        };
        var cloud = new FakeTranscriptionPlugin("com.typewhisper.openai", true, null)
        {
            ProvisionsCudaRuntimeOnDemand = false,
        };
        var sut = new ModelManagerService(
            CreatePluginManager(sherpa, whisper, cloud),
            _settings.Object
        );

        await sut.ClearCudaRuntimeCacheAsync();

        Assert.True(sherpa.ClearCudaRuntimeCalled);
        Assert.True(whisper.ClearCudaRuntimeCalled);
        // A non-provisioning engine has nothing to clear and must be left alone.
        Assert.False(cloud.ClearCudaRuntimeCalled);
    }

    [Fact]
    public async Task ClearCudaRuntimeCacheAsync_AttemptsAllEngines_ThenThrowsAggregate_OnFailure()
    {
        // A swallowed delete failure would tell the user the corrupt runtime was cleared
        // when it is still on disk. Every engine is still attempted, but the failure is
        // surfaced so the UI reports failure instead of a false success.
        _settings.Setup(s => s.Current).Returns(new AppSettings());

        var failing = new FakeTranscriptionPlugin("com.typewhisper.sherpa-onnx", true, null)
        {
            ProvisionsCudaRuntimeOnDemand = true,
            ClearCudaRuntimeError = "permission denied",
        };
        var succeeding = new FakeTranscriptionPlugin("com.typewhisper.whisper-cpp", true, null)
        {
            ProvisionsCudaRuntimeOnDemand = true,
        };
        var sut = new ModelManagerService(
            CreatePluginManager(failing, succeeding),
            _settings.Object
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ClearCudaRuntimeCacheAsync()
        );

        // The other engine was still attempted (failure of one doesn't abort the rest)...
        Assert.True(succeeding.ClearCudaRuntimeCalled);
        // ...and the failure is reported with the offending provider + reason.
        Assert.Contains("com.typewhisper.sherpa-onnx", ex.Message);
        Assert.Contains("permission denied", ex.Message);
    }

    private ModelManagerService CreateServiceWithLoadableModel(
        out string fullModelId,
        out ITranscriptionEnginePlugin plugin
    )
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        fullModelId = ModelManagerService.GetPluginModelId(pluginId, "parakeet");
        _settings.Setup(s => s.Current).Returns(new AppSettings { SelectedModelId = fullModelId });

        var fake = new FakeTranscriptionPlugin(
            pluginId,
            true,
            null,
            true
        );
        plugin = fake;
        var service = new ModelManagerService(CreatePluginManager(fake), _settings.Object);
        // Default to no CUDA so tests don't depend on the host having CUDA installed.
        service.CudaRuntimePreflight = () => (false, "CUDA not available in test");
        return service;
    }

    private PluginManager CreatePluginManager(
        params ITranscriptionEnginePlugin[] transcriptionEngines
    )
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []
        );

        SetPrivateField(pluginManager, "_transcriptionEngines", transcriptionEngines.ToList());
        return pluginManager;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field =
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            bool configured,
            string? selectedModelId,
            bool supportsModelDownload = false
        )
        {
            PluginId = pluginId;
            IsConfigured = configured;
            SelectedModelId = selectedModelId;
            SupportsModelDownload = supportsModelDownload;
            TranscriptionModels =
            [
                new PluginModelInfo("parakeet", "Parakeet"),
                new PluginModelInfo("whisper", "Whisper")
            ];
        }

        public string? LastLoadedModelId { get; private set; }

        /// <summary>Completes once <see cref="LoadModelAsync" /> has begun.</summary>
        public TaskCompletionSource LoadStarted { get; } = new();

        /// <summary>When set, <see cref="LoadModelAsync" /> parks until it completes.</summary>
        public TaskCompletionSource? LoadGate { get; set; }

        /// <summary>Completes once <see cref="UnloadModelAsync" /> has begun.</summary>
        public TaskCompletionSource UnloadStarted { get; } = new();

        /// <summary>When set, <see cref="UnloadModelAsync" /> parks until it completes.</summary>
        public TaskCompletionSource? UnloadGate { get; set; }

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

        public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; set; } =
            [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];

        public bool ProvisionsCudaRuntimeOnDemand { get; set; }

        /// <summary>Last preference passed to <see cref="SetAccelerationPreference" />.</summary>
        public TranscriptionAccelerationPreference? LastAccelerationPreference { get; private set; }

        /// <summary>Preference observed at the moment <see cref="LoadModelAsync" /> ran.</summary>
        public TranscriptionAccelerationPreference? AccelerationPreferenceAtLoad { get; private set; }

        public int SetAccelerationPreferenceCount { get; private set; }

        public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
        {
            LastAccelerationPreference = preference;
            SetAccelerationPreferenceCount++;
        }

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectModel(string modelId)
        {
            SelectedModelId = modelId;
        }

        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadStarted.TrySetResult();
            if (LoadGate is not null)
            {
                await LoadGate.Task.WaitAsync(ct);
            }

            LastLoadedModelId = modelId;
            SelectedModelId = modelId;
            AccelerationPreferenceAtLoad = LastAccelerationPreference;
        }

        public async Task UnloadModelAsync()
        {
            UnloadStarted.TrySetResult();
            if (UnloadGate is not null)
            {
                await UnloadGate.Task;
            }

            SelectedModelId = null;
        }

        /// <summary>When set, <see cref="ClearCudaRuntimeAsync" /> throws this message.</summary>
        public string? ClearCudaRuntimeError { get; set; }

        /// <summary>Whether <see cref="ClearCudaRuntimeAsync" /> was invoked.</summary>
        public bool ClearCudaRuntimeCalled { get; private set; }

        public Task ClearCudaRuntimeAsync(CancellationToken ct)
        {
            ClearCudaRuntimeCalled = true;
            if (ClearCudaRuntimeError is not null)
            {
                throw new InvalidOperationException(ClearCudaRuntimeError);
            }

            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            return Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));
        }

        public void Dispose() { }
    }
}