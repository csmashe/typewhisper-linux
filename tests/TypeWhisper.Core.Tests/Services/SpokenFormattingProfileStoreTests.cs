using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public sealed class SpokenFormattingProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"tw_spoken_profiles_{Guid.NewGuid():N}");

    [Fact]
    public void SaveReplaceClear_RoundTripsThroughSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Join(_directory, "settings.json");
        var store = new SpokenFormattingProfileStore(new SettingsService(path));
        store.SaveUserOverride(" engine ", " model ", "en-US", SpokenFormattingStrategy.NativeOnly,
            SpokenFormattingVerificationState.UserVerifiedGood, true);
        store = new SpokenFormattingProfileStore(new SettingsService(path));
        Assert.Equal(SpokenFormattingStrategy.NativeOnly, Assert.Single(store.Profiles).StrategyOverride);
        Assert.NotNull(store.Profiles[0].LastVerifiedAt);
        store.SaveUserOverride("engine", "model", "en", SpokenFormattingStrategy.FallbackOnly,
            SpokenFormattingVerificationState.UserVerifiedBad, true);
        store = new SpokenFormattingProfileStore(new SettingsService(path));
        Assert.Equal(SpokenFormattingStrategy.FallbackOnly, Assert.Single(store.Profiles).StrategyOverride);
        store.ClearUserOverride(" ENGINE ", " model ", "en-GB");
        Assert.Empty(new SettingsService(path).Current.SpokenFormattingProfiles);
    }

    [Fact]
    public void NormalizeProfiles_DeduplicatesAndPreservesUnknownValues()
    {
        var profile = new DictationSpokenFormattingProfile
        {
            EngineId = "engine", LanguageCode = "en", StrategyOverrideRaw = "future", VerificationStateRaw = "futureState",
        };
        var result = SpokenFormattingProfileStore.NormalizeProfiles([
            null, new DictationSpokenFormattingProfile { EngineId = "" }, profile with { LanguageCode = "en-US", StrategyOverrideRaw = "automatic" }, profile]);
        Assert.Equal(profile, Assert.Single(result));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
