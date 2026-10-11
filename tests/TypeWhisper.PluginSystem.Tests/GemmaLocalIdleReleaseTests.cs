using System.Security.Cryptography;
using Moq;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     An idle Gemma model is released after the host's delay and stays ready: the next request
///     loads it again. A seam stands in for the native load and a manual clock for the timer.
/// </summary>
public sealed class GemmaLocalIdleReleaseTests : IDisposable
{
    private const string ModelId = "test-model";
    private const string FileName = "test-model.gguf";
    private static readonly TimeSpan s_idle = TimeSpan.FromMinutes(10);

    private static readonly byte[] s_bytes = Bytes(4096);
    private static readonly GemmaModelDefinition s_model = new(
        ModelId,
        "Test Model",
        "~4 KB",
        1,
        true,
        "https://models.test/pinned/test-model.gguf",
        FileName,
        new GemmaModelFile(s_bytes.Length, Convert.ToHexStringLower(SHA256.HashData(s_bytes))),
        []);

    // Selectable but never downloaded, standing in for a switch whose download failed.
    private static readonly GemmaModelDefinition s_otherModel = s_model with { Id = "other-model" };

    private readonly string _root = Path.Join(Path.GetTempPath(), "tw-gemma-idle-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly Mock<IPluginHostServices> _host = new();
    private readonly List<string> _loads = [];
    private TimeSpan? _idleTimeout = s_idle;
    private Action? _onLoad;
    private Action? _onRequest;
    private string? _persistedSelection;
    private bool _useSystemClock;
    private bool _hostLacksIdleSetting;

    public GemmaLocalIdleReleaseTests()
    {
        var dir = Path.Join(_root, "Models", ModelId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Join(dir, FileName), s_bytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task IdleModel_IsReleasedAfterTheDelay_AndStaysReady()
    {
        using var plugin = await CreateLoadedAsync();

        _time.Advance(s_idle - TimeSpan.FromSeconds(1));
        Assert.Equal(ModelId, plugin.LoadedModelId);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(plugin.LoadedModelId);
        Assert.True(plugin.IsAvailable);
        Assert.Equal(ModelId, Assert.Single(plugin.SupportedModels).Id);
        Assert.True((await plugin.ValidateAsync())!.IsSuccess);
    }

    [Fact]
    public async Task NextRequest_LoadsTheReleasedModelAgain_AndRestartsTheWait()
    {
        using var plugin = await CreateLoadedAsync();
        _time.Advance(s_idle);

        await RequestAsync(plugin);

        Assert.Equal([ModelId, ModelId], _loads);
        Assert.Equal(ModelId, plugin.LoadedModelId);
        Assert.True(plugin.IdleReleaseArmed);
    }

    [Fact]
    public async Task Use_RestartsTheIdleWait()
    {
        using var plugin = await CreateLoadedAsync();

        _time.Advance(TimeSpan.FromMinutes(8));
        await RequestAsync(plugin);
        _time.Advance(TimeSpan.FromMinutes(8));
        Assert.Equal(ModelId, plugin.LoadedModelId);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task NoIdleDelay_KeepsTheModelLoaded()
    {
        _idleTimeout = null;
        using var plugin = await CreateLoadedAsync();

        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    [Fact]
    public async Task DelayEnabledLater_ReleasesAnAlreadyIdleModel()
    {
        _idleTimeout = null;
        using var plugin = await CreateLoadedAsync();
        _time.Advance(TimeSpan.FromHours(1));

        _idleTimeout = s_idle;
        _time.Advance(IdleReleaseTimer.s_recheckInterval);

        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task DelayShortened_WhileWaiting_AppliesWithinARecheck()
    {
        _idleTimeout = TimeSpan.FromHours(1);
        using var plugin = await CreateLoadedAsync();

        _idleTimeout = TimeSpan.FromSeconds(30);
        _time.Advance(IdleReleaseTimer.s_recheckInterval);

        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task DelayChangedToNever_WhileWaiting_KeepsTheModelLoaded()
    {
        using var plugin = await CreateLoadedAsync();

        _idleTimeout = null;
        _time.Advance(s_idle);

        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    [Fact]
    public async Task DelayLengthened_WhileWaiting_WaitsForTheNewDelay()
    {
        using var plugin = await CreateLoadedAsync();

        _idleTimeout = TimeSpan.FromMinutes(15);
        _time.Advance(s_idle);
        Assert.Equal(ModelId, plugin.LoadedModelId);
        Assert.True(plugin.IdleReleaseArmed);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task ReleaseDuringInference_WaitsAndRetries()
    {
        using var plugin = await CreateLoadedAsync();
        using var inRequest = new ManualResetEventSlim();
        using var finishRequest = new ManualResetEventSlim();
        _onRequest = () =>
        {
            // ReSharper disable once AccessToDisposedClosure -- runs inside the awaited request, before dispose.
            inRequest.Set();
            // ReSharper disable once AccessToDisposedClosure -- runs inside the awaited request, before dispose.
            finishRequest.Wait(TimeSpan.FromSeconds(10));
        };

        // ReSharper disable once AccessToDisposedClosure -- awaited before dispose.
        var request = Task.Run(() => RequestAsync(plugin));
        Assert.True(inRequest.Wait(TimeSpan.FromSeconds(10)));
        _time.Advance(s_idle);
        Assert.Equal(ModelId, plugin.LoadedModelId);
        Assert.True(plugin.IdleReleaseArmed);

        finishRequest.Set();
        await request;
        _time.Advance(IdleReleaseTimer.s_retryDelay);
        Assert.Equal(ModelId, plugin.LoadedModelId);

        _time.Advance(s_idle);
        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task ReleaseDuringALoad_WaitsAndRetries()
    {
        using var plugin = await CreateLoadedAsync();
        using var inLoad = new ManualResetEventSlim();
        using var finishLoad = new ManualResetEventSlim();
        _onLoad = () =>
        {
            // ReSharper disable once AccessToDisposedClosure -- runs inside the awaited load, before dispose.
            inLoad.Set();
            // ReSharper disable once AccessToDisposedClosure -- runs inside the awaited load, before dispose.
            finishLoad.Wait(TimeSpan.FromSeconds(10));
        };

        // Reselecting the same model while it is resident is not a reload, so force one.
        plugin.UnloadModel();
        var load = plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.True(inLoad.Wait(TimeSpan.FromSeconds(10)));
        _time.Advance(s_idle);
        Assert.True(plugin.IdleReleaseArmed);

        finishLoad.Set();
        await load;
        _onLoad = null;
        _time.Advance(IdleReleaseTimer.s_retryDelay);
        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    [Fact]
    public async Task FailedReload_MakesTheModelUnavailable_UntilItIsSelectedAgain()
    {
        using var plugin = await CreateLoadedAsync();
        _time.Advance(s_idle);
        _onLoad = () => throw new InvalidDataException("corrupt");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => plugin.ProcessAsync("system", "user", ModelId, CancellationToken.None));

        Assert.False(plugin.IsAvailable);
        Assert.Empty(plugin.SupportedModels);
        Assert.False((await plugin.ValidateAsync())!.IsSuccess);

        _onLoad = null;
        plugin.SelectModel(ModelId);
        Assert.True(plugin.IsAvailable);
        await RequestAsync(plugin);
        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    [Fact]
    public async Task SelectedDownload_LoadsOnTheFirstRequest_BeforeAnyExplicitLoad()
    {
        using var plugin = await CreateAsync();
        plugin.SelectModel(ModelId);
        Assert.True(plugin.IsAvailable);

        await RequestAsync(plugin);
        await plugin.LoadModelAsync(ModelId, CancellationToken.None);

        Assert.Equal([ModelId], _loads);
    }

    [Fact]
    public async Task ReleasingAModelLeftFromAFailedSwitch_NotifiesTheHost()
    {
        using var plugin = await CreateLoadedAsync();
        plugin.SelectModel(s_otherModel.Id);
        Assert.Equal(ModelId, Assert.Single(plugin.SupportedModels).Id);
        _host.Invocations.Clear();

        _time.Advance(s_idle);

        Assert.Null(plugin.LoadedModelId);
        Assert.False(plugin.IsAvailable);
        _host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Once);
    }

    [Fact]
    public async Task ClearingTheSelection_ForgetsTheModel()
    {
        using var plugin = await CreateLoadedAsync();

        await plugin.SetSettingValueAsync("selectedModel", "");

        Assert.False(plugin.IdleReleaseArmed);
        Assert.False(plugin.IsAvailable);
        Assert.Null(plugin.LoadedModelId);
    }

    [Fact]
    public async Task ClearedSelection_RestoredAfterRestart_IsUnavailableWithoutThrowing()
    {
        _persistedSelection = "";
        using var plugin = await CreateAsync();

        Assert.Null(plugin.SelectedModelId);
        Assert.False(plugin.IsAvailable);
        Assert.Empty(plugin.SupportedModels);
    }

    [Fact]
    public async Task OversizedIdleDelay_IsCappedAndNeverStrandsTheInferenceLock()
    {
        _idleTimeout = TimeSpan.FromSeconds(5_000_000);
        _useSystemClock = true;
        using var plugin = await CreateLoadedAsync();

        await RequestAsync(plugin);
        await RequestAsync(plugin);

        Assert.True(plugin.IdleReleaseArmed);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task HostWithoutTheIdleSetting_KeepsTheModelLoaded()
    {
        _hostLacksIdleSetting = true;
        using var plugin = await CreateLoadedAsync();

        await RequestAsync(plugin);
        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    [Fact]
    public async Task Deactivate_StopsTheIdleWait()
    {
        using var plugin = await CreateLoadedAsync();

        await plugin.DeactivateAsync();

        Assert.False(plugin.IdleReleaseArmed);
        Assert.Null(plugin.LoadedModelId);

        // A request that was queued behind the teardown must not load the model again.
        await RequestAsync(plugin);
        Assert.Equal([ModelId], _loads);
        Assert.Null(plugin.LoadedModelId);
    }

    private async Task<GemmaLocalPlugin> CreateLoadedAsync()
    {
        var plugin = await CreateAsync();
        plugin.SelectModel(ModelId);
        await plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.Equal(ModelId, plugin.LoadedModelId);
        Assert.True(plugin.IdleReleaseArmed);
        return plugin;
    }

    private async Task<GemmaLocalPlugin> CreateAsync()
    {
        var plugin = new GemmaLocalPlugin(
            null,
            (_, _) => _onRequest?.Invoke(),
            [s_model, s_otherModel],
            null,
            _useSystemClock ? null : _time)
        {
            NativeLoadOverride = id =>
            {
                _onLoad?.Invoke();
                _loads.Add(id);
            },
        };
        _host.Setup(h => h.PluginDataDirectory).Returns(_root);
        _host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        _host.Setup(h => h.LocalModelIdleTimeout).Returns(() =>
            _hostLacksIdleSetting ? throw new MissingMethodException("older host SDK") : _idleTimeout);
        _host.Setup(h => h.GetSetting<string>("selectedModel")).Returns(() => _persistedSelection);
        await plugin.ActivateAsync(_host.Object);
        return plugin;
    }

    // The seam loads no native context, so a request ends with "no model loaded" after its reload.
    private static async Task RequestAsync(GemmaLocalPlugin plugin)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => plugin.ProcessAsync("system", "user", ModelId, CancellationToken.None));
        Assert.StartsWith("No model loaded", error.Message, StringComparison.Ordinal);
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    // Timers fire synchronously on the thread that advances the clock.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now;

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += by.Ticks;
                due = _timers.Where(t => t.DueTicks <= _now).ToArray();
                foreach (var timer in due)
                    _timers.Remove(timer);
            }

            foreach (var timer in due)
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state, _now + dueTime.Ticks);
                _timers.Add(timer);
                return timer;
            }
        }

        public override long GetTimestamp()
        {
            lock (_gate)
                return _now;
        }

        // Timestamps are TimeSpan ticks.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long dueTicks)
            : ITimer
        {
            public long DueTicks { get; } = dueTicks;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
