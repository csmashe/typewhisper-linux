using System.Reflection;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resolves the running app version and compares SemVer-style version strings.
///     Shared by the About section and the update checker so both agree on version ordering.
/// </summary>
public static class AppVersion
{
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

        switch (aNumeric, bNumeric)
        {
            case (true, true):
                return an.CompareTo(bn);
            // Numeric identifiers rank below alphanumeric (SemVer §11.4).
            case (true, _):
                return -1;
            case (_, true):
                return 1;
            default:
                return string.CompareOrdinal(a, b);
        }
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