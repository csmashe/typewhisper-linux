using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>Resolves per-engine overrides over the global strategy and selects a supported rule language.</summary>
public sealed class SpokenFormattingStrategyResolver
{
    private readonly SpokenFormattingProfileStore _profileStore;
    private readonly SpokenFormattingRulesLoader _rulesLoader;

    /// <summary>Initializes strategy resolution with profile storage and language rules.</summary>
    public SpokenFormattingStrategyResolver(
        SpokenFormattingProfileStore profileStore,
        SpokenFormattingRulesLoader rulesLoader)
    {
        _profileStore = profileStore;
        _rulesLoader = rulesLoader;
    }

    /// <summary>Uses a sole configured language, then supported detected or configured languages; profile overrides win.</summary>
    public ResolvedSpokenFormattingStrategy Resolve(
        string? engineId,
        string? modelId,
        IReadOnlyList<string>? configuredLanguageCandidates,
        string? detectedLanguage,
        SpokenFormattingStrategy globalDefault)
    {
        var normalizedEngine = engineId?.Trim();
        if (string.IsNullOrEmpty(normalizedEngine))
            return new ResolvedSpokenFormattingStrategy(null, globalDefault, null, false);

        var languageCode = ResolveLanguage(configuredLanguageCandidates, detectedLanguage);
        if (languageCode is null)
            return new ResolvedSpokenFormattingStrategy(null, globalDefault, null, false);

        var normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var storedProfile = _profileStore.Profile(normalizedEngine, normalizedModel, languageCode);
        var profile = storedProfile ?? new DictationSpokenFormattingProfile
        {
            EngineId = normalizedEngine,
            ModelId = normalizedModel,
            LanguageCode = languageCode,
            VerificationStateRaw = DefaultVerificationState(normalizedModel, languageCode).ToRawValue(),
        };

        return new ResolvedSpokenFormattingStrategy(
            languageCode,
            profile.StrategyOverride ?? globalDefault,
            profile,
            true);
    }

    private string? ResolveLanguage(
        IReadOnlyList<string>? configuredLanguageCandidates,
        string? detectedLanguage)
    {
        var candidates = (configuredLanguageCandidates ?? [])
            .Select(SpokenFormattingLanguageNormalizer.Normalize)
            .Where(static language => language is not null)
            .Select(static language => language!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var detected = SpokenFormattingLanguageNormalizer.Normalize(detectedLanguage);
        var detectedSupported = detected is not null && _rulesLoader.Supports(detected);

        if (candidates.Count == 1)
            return _rulesLoader.Supports(candidates[0]) ? candidates[0] : null;
        return detectedSupported ? detected : candidates.FirstOrDefault(_rulesLoader.Supports);
    }

    private static SpokenFormattingVerificationState DefaultVerificationState(
        string? modelId,
        string languageCode) =>
        string.Equals(modelId, "parakeet-tdt-0.6b", StringComparison.OrdinalIgnoreCase)
        && string.Equals(languageCode, "de", StringComparison.OrdinalIgnoreCase)
            ? SpokenFormattingVerificationState.VendorHint
            : SpokenFormattingVerificationState.Unknown;
}
