using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the Enabled/Disabled tab split in the Plugins settings section:
///     rows are partitioned by their activation state, per-tab counts and
///     empty-state flags follow the partition, and <c>SetTab</c> toggles which
///     tab is selected.
/// </summary>
public sealed class PluginsSectionViewModelTests : IDisposable
{
    private static readonly string[] s_enabledIds = ["com.test.a", "com.test.b"];

    private readonly string _tempDir;

    public PluginsSectionViewModelTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "tw-vm-plugins-" + Guid.NewGuid().ToString("N"));
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
    public void Refresh_PartitionsRowsByEnabledState()
    {
        var vm = CreateVm(("com.test.a", true), ("com.test.b", true), ("com.test.c", false));

        var enabled = vm.EnabledGroups.SelectMany(g => g.Plugins).ToList();
        var disabled = vm.DisabledGroups.SelectMany(g => g.Plugins).ToList();

        Assert.All(enabled, row => Assert.True(row.IsEnabled));
        Assert.All(disabled, row => Assert.False(row.IsEnabled));
        Assert.Equal(
            s_enabledIds,
            enabled.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal)
        );
        Assert.Equal("com.test.c", Assert.Single(disabled).Id);
    }

    [Fact]
    public void Refresh_CountsReflectPartition()
    {
        var vm = CreateVm(("com.test.a", true), ("com.test.b", false), ("com.test.c", false));

        Assert.Equal(1, vm.EnabledCount);
        Assert.Equal(2, vm.DisabledCount);
        Assert.True(vm.HasEnabledPlugins);
        Assert.True(vm.HasDisabledPlugins);
    }

    [Fact]
    public void Refresh_EmptyPartitionClearsHasFlag()
    {
        var vm = CreateVm(("com.test.a", true));

        Assert.Equal(1, vm.EnabledCount);
        Assert.Equal(0, vm.DisabledCount);
        Assert.True(vm.HasEnabledPlugins);
        Assert.False(vm.HasDisabledPlugins);
    }

    [Fact]
    public void SetTab_TogglesSelectedTabFlags()
    {
        var vm = CreateVm(("com.test.a", true), ("com.test.b", false));

        // Default is the Enabled tab.
        Assert.Equal(0, vm.SelectedTab);
        Assert.True(vm.IsEnabledTabSelected);
        Assert.False(vm.IsDisabledTabSelected);

        // The button binds CommandParameter as a string, so mirror that here.
        vm.SetTabCommand.Execute("1");
        Assert.Equal(1, vm.SelectedTab);
        Assert.False(vm.IsEnabledTabSelected);
        Assert.True(vm.IsDisabledTabSelected);

        vm.SetTabCommand.Execute(0);
        Assert.Equal(0, vm.SelectedTab);
        Assert.True(vm.IsEnabledTabSelected);
        Assert.False(vm.IsDisabledTabSelected);
    }

    private PluginsSectionViewModel CreateVm(params (string Id, bool Enabled)[] plugins)
    {
        var loaded = plugins
            .Select(p =>
                TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, p.Id, new FakePlugin(p.Id))
            )
            .ToList();
        var activated = plugins.Where(p => p.Enabled).Select(p => p.Id);
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: loaded,
            activatedPluginIds: activated
        );
        return new PluginsSectionViewModel(manager);
    }

    // Minimal plugin: the section only needs manifest metadata plus the activation
    // state seeded on the manager, so no settings providers are implemented.
    private sealed class FakePlugin(string id) : ITypeWhisperPlugin
    {
        public string PluginId { get; } = id;
        public string PluginName => "Fake " + PluginId;
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
