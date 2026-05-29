using System.Reflection;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resolves the running app version and compares SemVer-style version
///     strings. Shared by the About section (display) and the update checker
///     (comparison against the latest GitHub release tag) so both agree on what
///     "the current version" is and how versions order.
/// </summary>
public static class AppVersion
{
    /// <summary>
    ///     The display version, e.g. "0.5.0" or "0.5.0-rc.1". Prefers
    ///     AssemblyInformationalVersion so a pre-release suffix survives
    ///     (AssemblyVersion is numeric-only and silently drops it); the +hash
    ///     SourceLink suffix isn't useful to users, so it's trimmed.
    /// </summary>
    public static string Display { get; } = Resolve();

    /// <summary>
    ///     The assembly copyright string (from AssemblyCopyrightAttribute, set
    ///     via &lt;Copyright&gt; in Directory.Build.props). Empty if unset.
    /// </summary>
    public static string Copyright { get; } =
        Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()
            ?.Copyright?.Trim() ?? string.Empty;

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
    ///     Compares two version strings (each optionally "v"-prefixed, with an
    ///     optional "-prerelease" and "+build" suffix). Returns &lt;0 when
    ///     <paramref name="a"/> is older, 0 when equal, &gt;0 when newer.
    ///     Build metadata is ignored; a release ranks above a pre-release of the
    ///     same numeric core (1.0.0 &gt; 1.0.0-rc.1), per SemVer 2.0.
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

        // Equal numeric core: a build with no pre-release is the final release
        // and outranks any pre-release of the same core.
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
    ///     Compares two non-empty pre-release strings per SemVer 2.0 §11:
    ///     dot-separated identifiers compared left to right; all-numeric
    ///     identifiers compared numerically and rank below alphanumeric ones;
    ///     alphanumeric compared in ASCII order; and when one is a prefix of the
    ///     other, the longer (more identifiers) ranks higher.
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

        if (aNumeric && bNumeric)
        {
            return an.CompareTo(bn);
        }

        // A numeric identifier always has lower precedence than alphanumeric.
        if (aNumeric)
        {
            return -1;
        }

        if (bNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(a, b);
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

        // Normalize to exactly three numeric components so System.Version's
        // "unspecified == -1" quirk can't make "0.5" sort below "0.5.0".
        var parts = s.Split('.');
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
        {
            nums[i] = i < parts.Length && int.TryParse(parts[i], out var n) && n >= 0 ? n : 0;
        }

        return (new Version(nums[0], nums[1], nums[2]), pre);
    }
}
