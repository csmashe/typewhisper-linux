using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>Reads and persists normalized per-engine formatting profiles through the settings service.</summary>
public sealed class SpokenFormattingProfileStore
{
    private readonly ISettingsService _settings;

    /// <summary>Initializes profile persistence with the settings service.</summary>
    public SpokenFormattingProfileStore(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Returns the currently persisted formatting profiles.</summary>
    public IReadOnlyList<DictationSpokenFormattingProfile> Profiles =>
        _settings.Current.SpokenFormattingProfiles;
    /// <summary>Finds a profile using normalized engine, model, and language identity.</summary>
    public DictationSpokenFormattingProfile? Profile(
        string engineId,
        string? modelId,
        string languageCode)
    {
        var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        var normalizedEngine = engineId.Trim();
        var normalizedModel = NormalizeOptional(modelId);
        if (normalizedLanguage is null || normalizedEngine.Length == 0)
            return null;

        var key = DictationSpokenFormattingProfile.MakeKey(
            normalizedEngine,
            normalizedModel,
            normalizedLanguage);
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Persists a normalized strategy override, optionally updating verification state and time.</summary>
    public void SaveUserOverride(
        string engineId,
        string? modelId,
        string languageCode,
        SpokenFormattingStrategy strategy,
        SpokenFormattingVerificationState? verificationState = null,
        bool updateVerificationDate = false)
    {
        var normalizedEngine = engineId.Trim();
        var normalizedModel = NormalizeOptional(modelId);
        var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        if (normalizedEngine.Length == 0 || normalizedLanguage is null)
            return;

        var existing = Profile(normalizedEngine, normalizedModel, normalizedLanguage);
        var updated = new DictationSpokenFormattingProfile
        {
            EngineId = normalizedEngine,
            ModelId = normalizedModel,
            LanguageCode = normalizedLanguage,
            StrategyOverrideRaw = strategy.ToRawValue(),
            VerificationStateRaw = (verificationState ?? existing?.VerificationState
                ?? SpokenFormattingVerificationState.Unknown).ToRawValue(),
            LastVerifiedAt = updateVerificationDate ? DateTime.UtcNow : existing?.LastVerifiedAt,
        };

        _settings.Update(current => current with
        {
            SpokenFormattingProfiles = NormalizeProfiles(current.SpokenFormattingProfiles
                .Where(profile => !string.Equals(profile.Key, updated.Key, StringComparison.OrdinalIgnoreCase))
                .Append(updated)),
        });
    }

    /// <summary>Removes the profile for the normalized engine, model, and language identity.</summary>
    public void ClearUserOverride(string engineId, string? modelId, string languageCode)
    {
        var language = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        if (language is null)
            return;

        var key = DictationSpokenFormattingProfile.MakeKey(engineId.Trim(), NormalizeOptional(modelId), language);
        _settings.Update(current => current with
        {
            SpokenFormattingProfiles = [.. current.SpokenFormattingProfiles
                .Where(profile => !string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase))],
        });
    }

    /// <summary>Normalizes identities and known values, retains unknown values, and keeps the last duplicate profile.</summary>
    public static IReadOnlyList<DictationSpokenFormattingProfile> NormalizeProfiles(
        IEnumerable<DictationSpokenFormattingProfile?>? profiles)
    {
        var normalized = new Dictionary<string, DictationSpokenFormattingProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles ?? [])
        {
            var engineId = profile?.EngineId.Trim();
            var languageCode = SpokenFormattingLanguageNormalizer.Normalize(profile?.LanguageCode);
            if (string.IsNullOrEmpty(engineId) || languageCode is null)
                continue;

            var strategyRaw = SpokenFormattingStrategyValues.TryParse(profile!.StrategyOverrideRaw, out var strategy)
                ? strategy.ToRawValue()
                : NormalizeOptional(profile.StrategyOverrideRaw);
            var verificationRaw = profile.VerificationStateRaw.Trim();
            var verificationStateRaw = SpokenFormattingVerificationStateValues.IsKnown(verificationRaw)
                ? SpokenFormattingVerificationStateValues.Parse(verificationRaw).ToRawValue()
                : string.IsNullOrEmpty(verificationRaw) ? "unknown" : verificationRaw;
            var item = profile with
            {
                EngineId = engineId,
                ModelId = NormalizeOptional(profile.ModelId),
                LanguageCode = languageCode,
                StrategyOverrideRaw = strategyRaw,
                VerificationStateRaw = verificationStateRaw,
            };
            normalized[item.Key] = item;
        }

        return normalized.Values.ToList();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
