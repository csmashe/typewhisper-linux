using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Choosing a default model on the Dictation page changes the engine's own model setting while
///     its plugin settings stay open, so showing the page again must not offer the old value to Save.
/// </summary>
public sealed class PluginSettingsRefreshOnShowTests : IDisposable
{
    private static readonly TimeSpan s_hangGuard = TimeSpan.FromSeconds(10);

    private readonly string _tempDir;

    public PluginSettingsRefreshOnShowTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "tw-vm-refresh-" + Guid.NewGuid().ToString("N"));
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
    public async Task RefreshExpandedSettings_ShowsModelChangedElsewhere()
    {
        var (vm, plugin, row) = await CreateExpandedAsync();

        plugin.Model = "universal-2";
        vm.RefreshExpandedSettings();
        await row.ObservedSettingsLoad.WaitAsync(s_hangGuard);

        Assert.Equal("universal-2", Field(row).Value);
        Assert.False(row.HasUnsavedSettings);
    }

    [Fact]
    public async Task RefreshExpandedSettings_KeepsUnsavedEdits()
    {
        var (vm, plugin, row) = await CreateExpandedAsync();
        Field(row).Value = "draft-model";

        plugin.Model = "universal-2";
        vm.RefreshExpandedSettings();
        await row.ObservedSettingsLoad.WaitAsync(s_hangGuard);

        Assert.Equal("draft-model", Field(row).Value);
    }

    [Fact]
    public async Task RefreshExpandedSettings_ClearsLoadError_WhenRetrySucceeds()
    {
        var (vm, plugin, row) = await CreateExpandedAsync();
        plugin.FailReads = true;
        vm.RefreshExpandedSettings();
        await row.ObservedSettingsLoad.WaitAsync(s_hangGuard);
        Assert.Equal("Unable to load plugin settings.", row.Status);
        Assert.Empty(row.SettingFields);

        plugin.FailReads = false;
        vm.RefreshExpandedSettings();
        await row.ObservedSettingsLoad.WaitAsync(s_hangGuard);

        Assert.Equal("universal-3-5-pro", Field(row).Value);
        Assert.Equal("Edit the values below and click Save.", row.Status);
    }

    [Fact]
    public async Task RefreshExpandedSettings_KeepsValidationResult()
    {
        var (vm, _, row) = await CreateExpandedAsync();
        await vm.ValidateSettingsCommand.ExecuteAsync(row);
        Assert.Equal("Model accepted.", row.Status);

        vm.RefreshExpandedSettings();
        await row.ObservedSettingsLoad.WaitAsync(s_hangGuard);

        Assert.Equal("Model accepted.", row.Status);
    }

    private async Task<(PluginsSectionViewModel Vm, ModelSettingsPlugin Plugin, PluginRow Row)> CreateExpandedAsync()
    {
        var plugin = new ModelSettingsPlugin();
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: [TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, plugin.PluginId, plugin)],
            activatedPluginIds: [plugin.PluginId]
        );
        var vm = new PluginsSectionViewModel(manager, null, TimeSpan.FromSeconds(5), postToUi: action => action());
        var row = vm.EnabledGroups.SelectMany(group => group.Plugins).Single(r => r.Id == plugin.PluginId);
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        Assert.Equal("universal-3-5-pro", Field(row).Value);
        return (vm, plugin, row);
    }

    private static PluginSettingFieldRow Field(PluginRow row) =>
        row.SettingFields.Single(field => field.Key == "selectedModel");

    private sealed class ModelSettingsPlugin : ITypeWhisperPlugin, IPluginSettingsProvider
    {
        public string PluginId => "com.test.model-settings";
        public string PluginName => "Model settings";
        public string PluginVersion => "1.0.0";
        public string Model { get; set; } = "universal-3-5-pro";

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "selectedModel",
                "Transcription model",
                Options:
                [
                    new PluginSettingOption("universal-3-5-pro", "Universal-3.5 Pro"),
                    new PluginSettingOption("universal-2", "Universal-2"),
                ]
            ),
        ];

        public bool FailReads { get; set; }

        public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
            FailReads
                ? Task.FromException<string?>(new InvalidOperationException("settings unavailable"))
                : Task.FromResult(key == "selectedModel" ? Model : null);

        public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
        {
            if (key == "selectedModel" && value is not null)
            {
                Model = value;
            }

            return Task.CompletedTask;
        }

        public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(true, "Model accepted.")
            );

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
