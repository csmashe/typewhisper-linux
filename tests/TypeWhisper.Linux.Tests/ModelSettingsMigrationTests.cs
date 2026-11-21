using Moq;
using TypeWhisper.Core.Interfaces;
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

    [Theory]
    [InlineData("plugin:com.typewhisper.voxtral:mistral-whisper")]
    [InlineData("plugin:com.typewhisper.voxtral:voxtral-mini-latest")]
    public void MigrateSettings_MovesEarlierVoxtralSelectionsToMiniTranscribe3(string earlier)
    {
        const string current = "plugin:com.typewhisper.voxtral:voxtral-mini-transcribe-3-0";
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings
        {
            SelectedModelId = earlier,
            FileTranscriptionModelOverride = earlier,
            WatchFolderModelOverride = "plugin:com.typewhisper.voxtral:voxtral-small-transcribe-3-0",
        });
        using var models = new ModelManagerService(TestPluginManagerFactory.Create(), settings.Object);

        models.MigrateSettings();

        Assert.Equal(current, settings.Object.Current.SelectedModelId);
        Assert.Equal(current, settings.Object.Current.FileTranscriptionModelOverride);
        Assert.Equal(
            "plugin:com.typewhisper.voxtral:voxtral-small-transcribe-3-0",
            settings.Object.Current.WatchFolderModelOverride);
    }

    [Fact]
    public void MigrateProfiles_MovesRetiredTranscriptionOverrides()
    {
        var profiles = new List<Profile>
        {
            new() { Id = "voxtral", Name = "Voxtral", TranscriptionModelOverride = "plugin:com.typewhisper.voxtral:voxtral-mini-latest" },
            new() { Id = "kept", Name = "Kept", TranscriptionModelOverride = "plugin:com.typewhisper.voxtral:voxtral-small-transcribe-3-0" },
            new() { Id = "global", Name = "Global" },
        };
        var service = new Mock<IProfileService>();
        service.SetupGet(s => s.Profiles).Returns(profiles);
        var updated = new List<Profile>();
        service.Setup(s => s.UpdateProfile(It.IsAny<Profile>())).Callback<Profile>(updated.Add);

        ModelManagerService.MigrateProfiles(service.Object);

        var profile = Assert.Single(updated);
        Assert.Equal("voxtral", profile.Id);
        Assert.Equal("plugin:com.typewhisper.voxtral:voxtral-mini-transcribe-3-0", profile.TranscriptionModelOverride);
    }
}
