using System.Reflection;
using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>Loads and caches embedded language rules, throwing when resources are missing or invalid.</summary>
public sealed class SpokenFormattingRulesLoader
{
    private static readonly string[] s_languages = ["de", "en", "es", "ru"];
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly Lazy<IReadOnlyDictionary<string, SpokenFormattingRuleSet>> s_cachedRuleSets =
        new(LoadRuleSets, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IReadOnlyDictionary<string, SpokenFormattingRuleSet> _ruleSets;

    /// <summary>Initializes the shared embedded rule cache.</summary>
    public SpokenFormattingRulesLoader()
    {
        _ruleSets = s_cachedRuleSets.Value;
    }

    private static Dictionary<string, SpokenFormattingRuleSet> LoadRuleSets()
    {
        var assembly = typeof(SpokenFormattingRulesLoader).Assembly;
        return s_languages
            .Select(language => LoadRuleSet(assembly, language))
            .ToDictionary(static ruleSet => ruleSet.Language, static ruleSet => ruleSet, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns the primary language codes with embedded rules.</summary>
    public IReadOnlyCollection<string> SupportedLanguages => _ruleSets.Keys.ToArray();
    /// <summary>Returns rules for a normalized language tag, or null when unsupported.</summary>
    public SpokenFormattingRuleSet? RuleSetFor(string? languageCode)
    {
        var normalized = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        return normalized is not null && _ruleSets.TryGetValue(normalized, out var ruleSet)
            ? ruleSet
            : null;
    }

    /// <summary>Reports whether a normalized language tag has embedded formatting rules.</summary>
    public bool Supports(string? languageCode) => RuleSetFor(languageCode) is not null;

    private static SpokenFormattingRuleSet LoadRuleSet(Assembly assembly, string language)
    {
        var resourceName = $"TypeWhisper.Core.Resources.SpokenFormatting.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        var ruleSet = JsonSerializer.Deserialize<SpokenFormattingRuleSet>(stream, s_jsonOptions);
        if (ruleSet is null
            || SpokenFormattingLanguageNormalizer.Normalize(ruleSet.Language) != language
            || ruleSet.Rules is not { Count: > 0 }
            || ruleSet.Rules.Any(static rule => string.IsNullOrWhiteSpace(rule.Phrase)
                || !Enum.IsDefined(rule.Category)))
        {
            throw new InvalidOperationException($"Invalid embedded resource: {resourceName}");
        }

        return ruleSet with { Language = language };
    }
}
