using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public class SpokenFormattingStrategyResolverTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SettingsService _settings;
    private readonly SpokenFormattingProfileStore _profileStore;
    private readonly SpokenFormattingStrategyResolver _sut;

    public SpokenFormattingStrategyResolverTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"tw_spoken_formatting_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _settings = new SettingsService(Path.Join(_tempDirectory, "settings.json"));
        _profileStore = new SpokenFormattingProfileStore(_settings);
        _sut = new SpokenFormattingStrategyResolver(_profileStore, new SpokenFormattingRulesLoader());
    }

    [Fact]
    public void Resolve_MissingProfile_DefaultsToAutomatic()
    {
        var result = _sut.Resolve("sherpa-onnx", "parakeet-tdt-0.6b", ["de"], null, SpokenFormattingStrategy.Automatic);

        Assert.NotNull(result);
        Assert.Equal("de", result.LanguageCode);
        Assert.Equal(SpokenFormattingStrategy.Automatic, result.Strategy);
        Assert.Equal(SpokenFormattingVerificationState.VendorHint, result.Profile!.VerificationState);
    }

    [Fact]
    public void Resolve_SingleConfiguredLanguageWinsOverDetectedLanguage()
    {
        var result = _sut.Resolve("engine", "model", ["de-DE"], "en-US", SpokenFormattingStrategy.Automatic);

        Assert.Equal("de", result.LanguageCode);
    }

    [Fact]
    public void Resolve_MultipleConfiguredLanguagesPreferDetectedLanguage()
    {
        var result = _sut.Resolve("engine", "model", ["de", "en"], "en-US", SpokenFormattingStrategy.Automatic);

        Assert.Equal("en", result.LanguageCode);
    }

    [Fact]
    public void Resolve_MultipleConfiguredLanguagesFallBackToFirstSupportedHint()
    {
        var result = _sut.Resolve("engine", "model", ["de", "en"], null, SpokenFormattingStrategy.Automatic);

        Assert.Equal("de", result.LanguageCode);
    }

    [Fact]
    public void Resolve_FreeAutoDetectionUsesDetectedSupportedLanguage()
    {
        var result = _sut.Resolve("engine", "model", [], "en-US", SpokenFormattingStrategy.Automatic);

        Assert.Equal("en", result.LanguageCode);
    }

    [Theory]
    [InlineData(SpokenFormattingStrategy.NativeOnly)]
    [InlineData(SpokenFormattingStrategy.Automatic)]
    [InlineData(SpokenFormattingStrategy.FallbackOnly)]
    public void Resolve_GlobalDefaultAndUnsupportedContext(SpokenFormattingStrategy strategy)
    {
        Assert.Equal(strategy, _sut.Resolve("engine", "model", ["en"], null, strategy).Strategy);
        string[] detectedLanguages = ["fr-FR", "de-DE"];
        foreach (var detected in detectedLanguages)
        {
            var result = _sut.Resolve("engine", "model", ["fr"], detected, strategy);
            Assert.False(result.RulesAvailable);
            Assert.Null(result.LanguageCode);
            Assert.Equal(strategy, result.Strategy);
        }
        Assert.False(_sut.Resolve(null, "model", ["de"], null, strategy).RulesAvailable);
        _profileStore.SaveUserOverride("engine", "model", "en", SpokenFormattingStrategy.NativeOnly);
        Assert.Equal(SpokenFormattingStrategy.NativeOnly, _sut.Resolve("engine", "model", ["en"], null, strategy).Strategy);
    }

    [Fact]
    public void SaveUserOverride_RoundTripsByEngineModelAndLanguage()
    {
        _profileStore.SaveUserOverride(
            " engine ",
            " model ",
            "DE-de",
            SpokenFormattingStrategy.FallbackOnly,
            SpokenFormattingVerificationState.UserVerifiedBad,
            updateVerificationDate: true);

        var reloadedSettings = new SettingsService(Path.Join(_tempDirectory, "settings.json"));
        var reloadedResolver = new SpokenFormattingStrategyResolver(
            new SpokenFormattingProfileStore(reloadedSettings),
            new SpokenFormattingRulesLoader());
        var result = reloadedResolver.Resolve("engine", "model", ["de"], null, SpokenFormattingStrategy.Automatic);

        Assert.Equal(SpokenFormattingStrategy.FallbackOnly, result.Strategy);
        Assert.Equal(SpokenFormattingVerificationState.UserVerifiedBad, result.Profile!.VerificationState);
        Assert.NotNull(result.Profile!.LastVerifiedAt);
    }

    [Fact]
    public void SettingsNormalization_PreservesUnknownFutureProfileValues()
    {
        _settings.Save(_settings.Current with
        {
            SpokenFormattingProfiles =
            [
                new DictationSpokenFormattingProfile
                {
                    EngineId = "engine",
                    ModelId = "model",
                    LanguageCode = "en-US",
                    StrategyOverrideRaw = "futureStrategy",
                    VerificationStateRaw = "futureVerification",
                },
            ],
        });

        var profile = Assert.Single(new SettingsService(Path.Join(_tempDirectory, "settings.json"))
            .Current.SpokenFormattingProfiles);

        Assert.Equal("futureStrategy", profile.StrategyOverrideRaw);
        Assert.Equal("futureVerification", profile.VerificationStateRaw);
        Assert.Null(profile.StrategyOverride);
        Assert.Equal(SpokenFormattingVerificationState.Unknown, profile.VerificationState);

        var resolver = new SpokenFormattingStrategyResolver(
            new SpokenFormattingProfileStore(new SettingsService(Path.Join(_tempDirectory, "settings.json"))),
            new SpokenFormattingRulesLoader());
        var resolved = resolver.Resolve("engine", "model", ["en"], null, SpokenFormattingStrategy.Automatic);

        Assert.Equal(SpokenFormattingStrategy.Automatic, resolved.Strategy);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
