using System.Diagnostics.CodeAnalysis;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     A validated transcription-language choice. Automatic selection is distinct from
///     an explicit, canonical BCP-47 language tag.
/// </summary>
// ReSharper disable once UnusedType.Global -- public plugin-SDK surface
public sealed record LanguageSelection
{
    private LanguageSelection(bool isAutomatic, string? languageTag)
    {
        IsAutomatic = isAutomatic;
        LanguageTag = languageTag;
    }

    /// <summary>Requests provider/model language detection.</summary>
    public static LanguageSelection Automatic { get; } = new(true, null);

    /// <summary>True when provider/model language detection was selected.</summary>
    public bool IsAutomatic { get; }

    /// <summary>The canonical BCP-47 tag for an explicit selection; otherwise null.</summary>
    public string? LanguageTag { get; }

    /// <summary>Creates an explicit selection from a valid BCP-47 tag.</summary>
    /// <exception cref="ArgumentException">
    ///     Thrown when <paramref name="languageTag" /> is blank, is the automatic sentinel,
    ///     or is not a supported BCP-47 form.
    /// </exception>
    public static LanguageSelection Explicit(string languageTag)
    {
        if (
            !TryParse(languageTag, out var selection)
            || selection.IsAutomatic
        )
        {
            throw new ArgumentException(
                "An explicit language selection requires a valid BCP-47 tag.",
                nameof(languageTag)
            );
        }

        return selection;
    }

    /// <summary>
    ///     Parses the case-insensitive <c>auto</c> sentinel or a pragmatic BCP-47 tag.
    ///     Blank input is not a language selection.
    /// </summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out LanguageSelection? selection
    )
    {
        selection = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            selection = Automatic;
            return true;
        }

        if (!TryCanonicalizeTag(trimmed, out var canonicalTag))
        {
            return false;
        }

        selection = new LanguageSelection(false, canonicalTag);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => IsAutomatic ? "auto" : LanguageTag!;

    private static bool TryCanonicalizeTag(string value, out string canonicalTag)
    {
        canonicalTag = string.Empty;
        var parts = value.Split('-');
        if (parts.Length == 0 || !IsAlpha(parts[0], 2, 3))
        {
            return false;
        }

        var canonicalParts = new List<string>(parts.Length)
        {
            parts[0].ToLowerInvariant(),
        };
        var index = 1;

        if (index < parts.Length && IsAlpha(parts[index], 4, 4))
        {
            var script = parts[index].ToLowerInvariant();
            canonicalParts.Add(char.ToUpperInvariant(script[0]) + script[1..]);
            index++;
        }

        if (
            index < parts.Length
            && (IsAlpha(parts[index], 2, 2) || IsDigit(parts[index], 3, 3))
        )
        {
            canonicalParts.Add(parts[index].ToUpperInvariant());
            index++;
        }

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < parts.Length && !string.Equals(parts[index], "x", StringComparison.OrdinalIgnoreCase))
        {
            var part = parts[index];
            var isVariant =
                IsAlphaNumeric(part, 5, 8)
                || (
                    part.Length == 4
                    && IsAsciiDigit(part[0])
                    && All(part.AsSpan(1), IsAsciiAlphaNumeric)
                );
            if (!isVariant || !variants.Add(part))
            {
                return false;
            }

            canonicalParts.Add(part.ToLowerInvariant());
            index++;
        }

        if (index < parts.Length)
        {
            canonicalParts.Add("x");
            index++;
            if (index == parts.Length)
            {
                return false;
            }

            for (; index < parts.Length; index++)
            {
                if (!IsAlphaNumeric(parts[index], 1, 8))
                {
                    return false;
                }

                canonicalParts.Add(parts[index].ToLowerInvariant());
            }
        }

        canonicalTag = string.Join('-', canonicalParts);
        return true;
    }

    private static bool IsAlpha(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && All(value, IsAsciiAlpha);

    private static bool IsDigit(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && All(value, IsAsciiDigit);

    private static bool IsAlphaNumeric(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && All(value, IsAsciiAlphaNumeric);

    private static bool All(ReadOnlySpan<char> value, Func<char, bool> predicate)
    {
        foreach (var character in value)
        {
            if (!predicate(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlpha(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool IsAsciiAlphaNumeric(char value) =>
        IsAsciiAlpha(value) || IsAsciiDigit(value);
}
