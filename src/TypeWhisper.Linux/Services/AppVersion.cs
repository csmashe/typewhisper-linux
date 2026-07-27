using System.Reflection;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resolves the running app version and compares SemVer-style version strings.
///     Shared by the About section and the update checker so both agree on version ordering.
/// </summary>
public static class AppVersion
{
    internal readonly record struct StrictSemanticVersion(
        string Major,
        string Minor,
        string Patch,
        IReadOnlyList<string> PreRelease
    );

    /// <summary>
    ///     Display version, e.g. "0.5.0" or "0.5.0-rc.1". Uses AssemblyInformationalVersion
    ///     so pre-release suffixes survive (AssemblyVersion silently drops them); the +hash
    ///     SourceLink suffix is trimmed.
    /// </summary>
    public static string Display { get; } = Resolve();

    /// <summary>Assembly copyright string from <c>&lt;Copyright&gt;</c> in Directory.Build.props. Empty if unset.</summary>
    public static string Copyright { get; } =
        Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Copyright is read from assembly metadata via reflection; keep the defensive null-conditional even though the BCL annotates it non-null
            ?.Copyright?.Trim() ?? string.Empty;

    /// <summary>
    ///     Compares two SemVer-style strings (optional "v" prefix, "-prerelease", "+build").
    ///     Returns &lt;0/0/&gt;0 for older/equal/newer. Build metadata ignored; release outranks
    ///     a pre-release of the same core (1.0.0 &gt; 1.0.0-rc.1), per SemVer 2.0.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        var core = coreA.CompareTo(coreB);
        if (core != 0)
        {
            return core;
        }

        // No pre-release = final release = outranks any pre-release of the same core.
        if (string.IsNullOrEmpty(preA) && string.IsNullOrEmpty(preB))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(preA))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(preB))
        {
            return -1;
        }

        return ComparePreRelease(preA, preB);
    }

    /// <summary>
    ///     Parses a strict SemVer 2.0 version: exactly major.minor.patch, with optional
    ///     pre-release and build metadata. Leading zeroes in numeric core or pre-release
    ///     identifiers and malformed/empty identifiers are rejected.
    /// </summary>
    internal static bool TryParseStrict(string? raw, out StrictSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var versionPart = raw;
        var plus = versionPart.IndexOf('+');
        if (plus >= 0)
        {
            if (
                versionPart.IndexOf('+', plus + 1) >= 0
                || !AreValidIdentifiers(versionPart[(plus + 1)..], false)
            )
            {
                return false;
            }

            versionPart = versionPart[..plus];
        }

        var preRelease = Array.Empty<string>();
        var dash = versionPart.IndexOf('-');
        if (dash >= 0)
        {
            var rawPreRelease = versionPart[(dash + 1)..];
            if (!AreValidIdentifiers(rawPreRelease, true))
            {
                return false;
            }

            preRelease = rawPreRelease.Split('.');
            versionPart = versionPart[..dash];
        }

        var core = versionPart.Split('.');
        if (
            core.Length != 3
            || !IsValidCoreIdentifier(core[0])
            || !IsValidCoreIdentifier(core[1])
            || !IsValidCoreIdentifier(core[2])
        )
        {
            return false;
        }

        version = new StrictSemanticVersion(core[0], core[1], core[2], preRelease);
        return true;
    }

    /// <summary>
    ///     Strictly parses and compares two SemVer 2.0 versions. Returns false when either
    ///     input is malformed; otherwise comparison is &lt;0/0/&gt;0 for older/equal/newer.
    /// </summary>
    internal static bool TryCompareStrict(string? a, string? b, out int comparison)
    {
        comparison = 0;
        if (!TryParseStrict(a, out var parsedA) || !TryParseStrict(b, out var parsedB))
        {
            return false;
        }

        comparison = CompareStrict(parsedA, parsedB);
        return true;
    }

    /// <summary>
    ///     Applies the plugin minimum-host rule. A blank minimum accepts any host;
    ///     malformed non-blank minima and hosts fail closed.
    /// </summary>
    internal static bool IsHostCompatible(
        string? minimumHostVersion,
        string hostVersion,
        out string reason
    )
    {
        if (string.IsNullOrWhiteSpace(minimumHostVersion))
        {
            reason = string.Empty;
            return true;
        }

        if (!TryParseStrict(minimumHostVersion, out var minimum))
        {
            reason = $"Minimum host version '{minimumHostVersion}' is not valid SemVer.";
            return false;
        }

        if (!TryParseStrict(hostVersion, out var host))
        {
            reason =
                $"Host version '{hostVersion}' is not valid SemVer, so compatibility cannot be verified.";
            return false;
        }

        if (CompareStrict(host, minimum) >= 0)
        {
            reason = string.Empty;
            return true;
        }

        reason =
            $"Requires host version '{minimumHostVersion}' or later; current host version is '{hostVersion}'.";
        return false;
    }

    private static string Resolve()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
        {
            return asm.GetName().Version?.ToString(3) ?? "dev";
        }

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    private static int CompareStrict(StrictSemanticVersion a, StrictSemanticVersion b)
    {
        var core = CompareNumericIdentifier(a.Major, b.Major);
        if (core == 0)
        {
            core = CompareNumericIdentifier(a.Minor, b.Minor);
        }

        if (core == 0)
        {
            core = CompareNumericIdentifier(a.Patch, b.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- independent pre-release guard chain over two operands; no single value to switch on.
        if (a.PreRelease.Count == 0 && b.PreRelease.Count == 0)
        {
            return 0;
        }

        if (a.PreRelease.Count == 0)
        {
            return 1;
        }

        if (b.PreRelease.Count == 0)
        {
            return -1;
        }

        var shared = Math.Min(a.PreRelease.Count, b.PreRelease.Count);
        for (var i = 0; i < shared; i++)
        {
            var aIdentifier = a.PreRelease[i];
            var bIdentifier = b.PreRelease[i];
            var aNumeric = IsAsciiDigits(aIdentifier);
            var bNumeric = IsAsciiDigits(bIdentifier);

            var identifier = (aNumeric, bNumeric) switch
            {
                (true, true) => CompareNumericIdentifier(aIdentifier, bIdentifier),
                (true, _) => -1,
                (_, true) => 1,
                _ => string.CompareOrdinal(aIdentifier, bIdentifier),
            };
            if (identifier != 0)
            {
                return identifier;
            }
        }

        return a.PreRelease.Count.CompareTo(b.PreRelease.Count);
    }

    private static int CompareNumericIdentifier(string a, string b)
    {
        var length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    }

    private static bool IsValidCoreIdentifier(string value)
    {
        return IsAsciiDigits(value) && (value.Length == 1 || value[0] != '0');
    }

    private static bool AreValidIdentifiers(string value, bool rejectNumericLeadingZeroes)
    {
        if (value.Length == 0)
        {
            return false;
        }

        // ReSharper disable once LoopCanBeConvertedToQuery -- the reject condition is a multi-line boolean; an All(...) lambda would read worse.
        foreach (var identifier in value.Split('.'))
        {
            if (
                identifier.Length == 0
                || !identifier.All(IsSemVerIdentifierCharacter)
                || (
                    rejectNumericLeadingZeroes
                    && identifier.Length > 1
                    && identifier[0] == '0'
                    && IsAsciiDigits(identifier)
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiDigits(string value)
    {
        return value.Length > 0 && value.All(c => c is >= '0' and <= '9');
    }

    private static bool IsSemVerIdentifierCharacter(char value)
    {
        return value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '-';
    }

    /// <summary>
    ///     SemVer 2.0 §11 pre-release comparison: dot-separated identifiers left-to-right;
    ///     numeric identifiers compared numerically and rank below alphanumeric;
    ///     more identifiers wins when one is a prefix of the other.
    /// </summary>
    private static int ComparePreRelease(string a, string b)
    {
        var idsA = a.Split('.');
        var idsB = b.Split('.');
        var shared = Math.Min(idsA.Length, idsB.Length);

        for (var i = 0; i < shared; i++)
        {
            var cmp = CompareIdentifier(idsA[i], idsB[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return idsA.Length.CompareTo(idsB.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = long.TryParse(a, out var an);
        var bNumeric = long.TryParse(b, out var bn);

        return (aNumeric, bNumeric) switch
        {
            (true, true) => an.CompareTo(bn),
            // Numeric identifiers rank below alphanumeric (SemVer §11.4).
            (true, _) => -1,
            (_, true) => 1,
            _ => string.CompareOrdinal(a, b),
        };
    }

    private static (Version Core, string PreRelease) Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (new Version(0, 0, 0), string.Empty);
        }

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var pre = string.Empty;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        // Normalize to exactly three components: System.Version treats unspecified as -1,
        // which would make "0.5" sort below "0.5.0" without this.
        var parts = s.Split('.');
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
        {
            nums[i] = i < parts.Length && int.TryParse(parts[i], out var n) && n >= 0 ? n : 0;
        }

        return (new Version(nums[0], nums[1], nums[2]), pre);
    }
}
