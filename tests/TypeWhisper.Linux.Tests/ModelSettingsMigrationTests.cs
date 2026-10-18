using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ModelSettingsMigrationTests
{
    [Fact]
    public void MigrateSettings_MovesRetiredAssemblyAiProSelectionsToUniversal35Pro()
    {
        const string retired = "plugin:com.typewhisper.assemblyai:universal-3-pro";
        const string current = "plugin:com.typewhisper.assemblyai:universal-3-5-pro";
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings
        {
            SelectedModelId = retired,
            FileTranscriptionModelOverride = retired,
            WatchFolderModelOverride = "plugin:com.typewhisper.assemblyai:universal-2",
        });
        using var models = new ModelManagerService(TestPluginManagerFactory.Create(), settings.Object);

        models.MigrateSettings();

        Assert.Equal(current, settings.Object.Current.SelectedModelId);
        Assert.Equal(current, settings.Object.Current.FileTranscriptionModelOverride);
        Assert.Equal("plugin:com.typewhisper.assemblyai:universal-2", settings.Object.Current.WatchFolderModelOverride);
    }
}
