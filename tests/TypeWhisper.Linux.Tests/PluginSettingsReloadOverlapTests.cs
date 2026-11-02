using System.Reflection;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     A provider's validation commits its catalog and raises <c>PluginStateChanged</c>, which
///     rebuilds the rows and starts an ambient settings load on the replacement row. Validate's
///     own completion reload then lands on that same row while the ambient load is still
///     reading values, and both used to append a full set of controls.
/// </summary>
public sealed class PluginSettingsReloadOverlapTests : IDisposable
{
    private static readonly TimeSpan s_hangGuard = TimeSpan.FromSeconds(10);

    private readonly string _tempDir;

    public PluginSettingsReloadOverlapTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "tw-vm-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public async Task Validate_WhoseCatalogCommitRebuildsRows_RendersEachSettingOnce()
    {
        var plugin = new NotifyingSettingsPlugin();
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: [TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, plugin.PluginId, plugin)],
            activatedPluginIds: [plugin.PluginId]
        );
        plugin.Manager = manager;
        var vm = new PluginsSectionViewModel(manager, null, TimeSpan.FromSeconds(5), postToUi: action => action());
        var row = Row(vm, plugin.PluginId);

        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        Assert.Equal(["api-key", "stream"], row.SettingFields.Select(field => field.Key));

        // Hold every value read so the ambient load and the completion reload overlap for sure.
        plugin.HoldReads = true;
        var bothLoadsReading = plugin.WaitForHeldReadsAsync(2);
        var validate = vm.ValidateSettingsCommand.ExecuteAsync(row);
        await bothLoadsReading.WaitAsync(s_hangGuard);
        plugin.ReleaseReads();
        await validate.WaitAsync(s_hangGuard);

        var current = Row(vm, plugin.PluginId);
        // The ambient load the rebuild started is fire-and-forget; await it before counting.
        await current.ObservedSettingsLoad.WaitAsync(s_hangGuard);

        Assert.NotSame(row, current);
        Assert.True(current.IsExpanded);
        Assert.Equal(["api-key", "stream"], current.SettingFields.Select(field => field.Key));
        Assert.True(current.CanEditSettings);
    }

    private static PluginRow Row(PluginsSectionViewModel vm, string pluginId) =>
        vm.EnabledGroups.Concat(vm.DisabledGroups)
            .SelectMany(group => group.Plugins)
            .DistinctBy(row => row.Id)
            .Single(row => row.Id == pluginId);

    // Mirrors a provider whose ValidateAsync commits a fetched catalog and calls
    // host.NotifyCapabilitiesChanged(), which the host turns into PluginStateChanged.
    private sealed class NotifyingSettingsPlugin : ITypeWhisperPlugin, IPluginSettingsProvider
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _heldReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _heldReads;
        private int _heldTarget;

        public string PluginId => "com.test.notifying";
        public string PluginName => "Notifying";
        public string PluginVersion => "1.0.0";
        public PluginManager? Manager { get; set; }
        public bool HoldReads { get; set; }

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new("api-key", "API key", IsSecret: true),
            new("stream", "Stream responses", Kind: PluginSettingKind.Boolean),
        ];

        public async Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
        {
            Task release;
            lock (_gate)
            {
                if (!HoldReads)
                {
                    return key == "stream" ? "true" : "sk-saved";
                }

                _heldReads++;
                _firstHeld.TrySetResult();
                if (_heldReads >= _heldTarget)
                {
                    _heldReached.TrySetResult();
                }

                release = _release.Task;
            }

            await release.WaitAsync(ct);
            return key == "stream" ? "true" : "sk-saved";
        }

        public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
        {
            var handler = (EventHandler?)typeof(PluginManager)
                .GetField(nameof(PluginManager.PluginStateChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Manager!);
            handler?.Invoke(Manager, EventArgs.Empty);
            // Return only once the ambient load is parked in a value read, so the completion
            // reload always starts mid-load instead of sometimes beating it to its first await.
            if (HoldReads)
            {
                await _firstHeld.Task.WaitAsync(ct);
            }

            return new PluginSettingsValidationResult(true, "valid");
        }

        public Task WaitForHeldReadsAsync(int count)
        {
            lock (_gate)
            {
                _heldTarget = count;
                if (_heldReads >= count)
                {
                    _heldReached.TrySetResult();
                }

                return _heldReached.Task;
            }
        }

        public void ReleaseReads()
        {
            lock (_gate)
            {
                HoldReads = false;
                _release.TrySetResult();
            }
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
