using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>Selects native output, legacy high-confidence passes, or full local fallback rules.</summary>
public enum SpokenFormattingStrategy
{
    /// <summary>Preserves engine output without local spoken formatting.</summary>
    NativeOnly,
    /// <summary>Runs the legacy line-break and question/exclamation passes.</summary>
    Automatic,
    /// <summary>Runs local rules for the resolved language, falling back to Automatic when no rules exist.</summary>
    FallbackOnly,
}

/// <summary>Records the source and outcome of an engine formatting assessment.</summary>
public enum SpokenFormattingVerificationState
{
    /// <summary>No formatting assessment is available.</summary>
    Unknown,
    /// <summary>A vendor hint suggests testing the engine.</summary>
    VendorHint,
    /// <summary>The user confirmed native formatting works.</summary>
    UserVerifiedGood,
    /// <summary>The user confirmed local fallback is needed.</summary>
    UserVerifiedBad,
}

/// <summary>Identifies the kind of command and its replacement spacing behavior.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpokenFormattingRuleCategory>))]
public enum SpokenFormattingRuleCategory
{
    /// <summary>A punctuation command with duplicate-mark suppression.</summary>
    Punctuation,
    /// <summary>A bracket command with opening or closing placement.</summary>
    // ReSharper disable once UnusedMember.Global -- Public enum value bound from JSON bracket rules.
    Brackets,
    /// <summary>A quotation-mark command with opening or closing placement.</summary>
    // ReSharper disable once UnusedMember.Global -- Public enum value bound from JSON quotation rules.
    Quotes,
    /// <summary>A layout command producing tabs or line breaks.</summary>
    Structural,
}

/// <summary>Controls spacing at the opening or closing side of a replacement.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpokenFormattingRulePlacement>))]
public enum SpokenFormattingRulePlacement
{
    /// <summary>No opening or closing placement is applied.</summary>
    None,
    /// <summary>Places an opening mark next to the following content.</summary>
    Open,
    /// <summary>Places a closing mark next to the preceding content.</summary>
    Close,
}

/// <summary>Maps a spoken phrase to a replacement with local spacing semantics.</summary>
public sealed record SpokenFormattingRule
{
    /// <summary>The spoken command phrase matched without regard to case.</summary>
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Initialized by JSON rule binding.
    public string Phrase { get; init; } = "";
    /// <summary>The literal punctuation or layout text emitted for the command.</summary>
    public string Replacement { get; init; } = "";
    /// <summary>The command category controlling replacement behavior.</summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global -- Initialized by JSON rule binding.
    public SpokenFormattingRuleCategory Category { get; init; }

    private readonly SpokenFormattingRulePlacement? _placement;

    /// <summary>Uses explicit placement when supplied; otherwise infers it from known bracket and quote characters.</summary>
    [JsonPropertyName("placement")]
    public SpokenFormattingRulePlacement Placement
    {
        get => _placement ?? Replacement switch
        {
            "(" or "[" or "{" or "“" or "„" or "«" or "‹" => SpokenFormattingRulePlacement.Open,
            ")" or "]" or "}" or "”" or "»" or "›" => SpokenFormattingRulePlacement.Close,
            _ => SpokenFormattingRulePlacement.None,
        };
        // ReSharper disable once UnusedMember.Global -- Optional placement is initialized by JSON rule binding.
        init => _placement = value;
    }
}

/// <summary>Pairs a dictated example with its expected formatted output.</summary>
// ReSharper disable once ClassNeverInstantiated.Global -- Instantiated by JSON verification scenario binding.
public sealed record SpokenFormattingVerificationScenario
{
    /// <summary>The phrase to dictate when verifying engine formatting.</summary>
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Initialized by JSON verification scenario binding.
    public string Spoken { get; init; } = "";
    /// <summary>The expected text after spoken formatting.</summary>
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Initialized by JSON verification scenario binding.
    public string Expected { get; init; } = "";
}

/// <summary>Contains local formatting rules and verification examples for a language.</summary>
public sealed record SpokenFormattingRuleSet
{
    /// <summary>The primary language code for this rule set.</summary>
    public string Language { get; init; } = "";
    /// <summary>The commands supported by this language.</summary>
    public IReadOnlyList<SpokenFormattingRule> Rules { get; init; } = [];
    /// <summary>The examples offered for manual engine verification.</summary>
    public IReadOnlyList<SpokenFormattingVerificationScenario> VerificationScenarios { get; init; } = [];
}

/// <summary>Persists an engine/model/language override and verification state, retaining unknown raw values.</summary>
public sealed record DictationSpokenFormattingProfile
{
    /// <summary>The transcription engine provider identifier.</summary>
    public string EngineId { get; init; } = "";
    /// <summary>The model identifier, or null for the engine default.</summary>
    public string? ModelId { get; init; }
    /// <summary>The normalized primary language code.</summary>
    public string LanguageCode { get; init; } = "";
    /// <summary>The persisted strategy value, including unrecognized values for forward compatibility.</summary>
    public string? StrategyOverrideRaw { get; init; }
    /// <summary>The persisted verification value, including unrecognized values for forward compatibility.</summary>
    public string VerificationStateRaw
    {
        get;
        // JsonSerializer passes null for a null JSON value; fall back to "unknown" here so every
        // reader, the store's normalization included, sees a string.
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        init => field = value ?? "unknown";
    } = "unknown";
    /// <summary>The UTC time of the last user verification, when recorded.</summary>
    public DateTime? LastVerifiedAt { get; init; }

    /// <summary>The recognized strategy override, or null for absent or unknown values.</summary>
    [JsonIgnore]
    public SpokenFormattingStrategy? StrategyOverride =>
        SpokenFormattingStrategyValues.TryParse(StrategyOverrideRaw, out var strategy)
            ? strategy
            : null;

    /// <summary>The parsed verification state, defaulting to Unknown for unrecognized values.</summary>
    [JsonIgnore]
    public SpokenFormattingVerificationState VerificationState =>
        SpokenFormattingVerificationStateValues.Parse(VerificationStateRaw);

    /// <summary>The composite identity used to match persisted profiles.</summary>
    [JsonIgnore]
    public string Key => MakeKey(EngineId, ModelId, LanguageCode);

    /// <summary>Builds an identity from engine, optional model, and language; callers normalize each component.</summary>
    public static string MakeKey(string engineId, string? modelId, string languageCode) =>
        $"{engineId}::{modelId ?? "__default__"}::{languageCode}";
}

/// <summary>Carries the resolved language, effective strategy, profile, and availability of local rules.</summary>
/// <param name="LanguageCode">The selected primary language, or null when no rules can be resolved.</param>
/// <param name="Strategy">The profile override or global default used by the pipeline.</param>
/// <param name="Profile">The stored or inferred engine profile, when a language is resolved.</param>
/// <param name="RulesAvailable">Whether the selected language has local fallback rules.</param>
public sealed record ResolvedSpokenFormattingStrategy(
    string? LanguageCode,
    SpokenFormattingStrategy Strategy,
    DictationSpokenFormattingProfile? Profile,
    bool RulesAvailable);

/// <summary>Converts strategy overrides to and from their persisted string values.</summary>
public static class SpokenFormattingStrategyValues
{
    /// <summary>Parses a known strategy value case-insensitively, ignoring surrounding whitespace.</summary>
    public static bool TryParse(string? rawValue, out SpokenFormattingStrategy strategy)
    {
        switch (rawValue?.Trim().ToLowerInvariant())
        {
            case "nativeonly":
                strategy = SpokenFormattingStrategy.NativeOnly;
                return true;
            case "automatic":
                strategy = SpokenFormattingStrategy.Automatic;
                return true;
            case "fallbackonly":
                strategy = SpokenFormattingStrategy.FallbackOnly;
                return true;
            default:
                strategy = SpokenFormattingStrategy.NativeOnly;
                return false;
        }
    }

    /// <summary>Returns the canonical persisted string for the value.</summary>
    public static string ToRawValue(this SpokenFormattingStrategy strategy) => strategy switch
    {
        SpokenFormattingStrategy.NativeOnly => "nativeOnly",
        SpokenFormattingStrategy.FallbackOnly => "fallbackOnly",
        _ => "automatic",
    };
}

/// <summary>Converts verification states while allowing profiles to retain unknown raw values.</summary>
public static class SpokenFormattingVerificationStateValues
{
    /// <summary>Reports whether a persisted verification value is recognized.</summary>
    public static bool IsKnown(string? rawValue) => rawValue?.Trim().ToLowerInvariant() is
        "unknown" or "vendorhint" or "userverifiedgood" or "userverifiedbad";
    /// <summary>Parses a verification value, returning Unknown for absent or unrecognized values.</summary>
    public static SpokenFormattingVerificationState Parse(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "vendorhint" => SpokenFormattingVerificationState.VendorHint,
            "userverifiedgood" => SpokenFormattingVerificationState.UserVerifiedGood,
            "userverifiedbad" => SpokenFormattingVerificationState.UserVerifiedBad,
            _ => SpokenFormattingVerificationState.Unknown,
        };

    /// <summary>Returns the canonical persisted string for the value.</summary>
    public static string ToRawValue(this SpokenFormattingVerificationState state) => state switch
    {
        SpokenFormattingVerificationState.VendorHint => "vendorHint",
        SpokenFormattingVerificationState.UserVerifiedGood => "userVerifiedGood",
        SpokenFormattingVerificationState.UserVerifiedBad => "userVerifiedBad",
        _ => "unknown",
    };
}

/// <summary>Normalizes language tags for rule lookup and profile identities.</summary>
public static class SpokenFormattingLanguageNormalizer
{
    /// <summary>Returns a lowercase primary language code, or null for empty and automatic selections.</summary>
    public static string? Normalize(string? languageCode)
    {
        var value = languageCode?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var primary = value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(primary) ? null : primary.ToLowerInvariant();
    }
}
